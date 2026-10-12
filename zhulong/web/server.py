"""只读面板服务 + 令牌守卫的动作通道。
红线修订：面板仍零"配置写"端点；仅 POST /api/action 一个动作端点——
CSRF token（同源页面才可读）+ Host/Origin 校验（防 DNS rebinding）+ 动作白名单 + kill 七步安全链。
v1.5 B2：GET 系端点同样过 Host 校验（令牌端点此前无 Host 检查，DNS rebinding 可读令牌）；
Origin 改精确比较 hostname；GET 参数钳制；静态资源内存缓存 + Cache-Control。"""
import json
import logging
import secrets
import time
import urllib.parse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

from zhulong import config
from zhulong.core import storage

LOG = logging.getLogger("zhulong")
ACTION_TOKEN = secrets.token_hex(16)   # 进程级一次性动作令牌（GET /api/action_token 仅同源可读）

STATIC = Path(__file__).parent / "static"
_STATIC_NAMES = ("index.html", "chart.umd.js", "panel.css", "panel.js", "favicon.svg")
_MIME = {".html": "text/html; charset=utf-8", ".js": "text/javascript; charset=utf-8",
         ".css": "text/css; charset=utf-8", ".svg": "image/svg+xml"}
_ALLOWED_HOSTS = ("127.0.0.1", "localhost")
_CURRENT_KEYS = [("memory", "commit_percent"), ("memory", "commit_used_gb"),
                 ("memory", "commit_limit_gb"), ("memory", "mem_percent"),
                 ("memory", "mem_used_gb"), ("memory", "mem_total_gb"),
                 ("memory", "pagefile_used_gb"), ("memory", "pagefile_total_gb"),
                 ("cpu", "cpu_percent"), ("cpu", "cpu_max_core"),
                 ("diskio", "io_read_mb_s"), ("diskio", "io_write_mb_s"),
                 ("net", "net_down_mb_s"), ("net", "net_up_mb_s"),
                 ("net", "ping_gw_ms"), ("net", "ping_net_ms"), ("net", "ping_jitter_ms"),
                 ("gpu", "gpu_temp"), ("gpu", "gpu_util"), ("gpu", "gpu_mem_used"), ("gpu", "gpu_mem_total"),
                 ("proctop", "cpu_top_pct"), ("proctop", "io_top_mb_s"),
                 ("proctop", "proctop1"), ("proctop", "proctop2"), ("proctop", "proctop3"),
                 ("proctop", "proctop4"), ("proctop", "proctop5")]


def _clamp_int(q, name, default, lo, hi):
    """GET 参数解析 + 钳制到 [lo, hi]；无法解析（如 hours=abc）抛 ValueError，调用方转 400。"""
    raw = q.get(name, [None])[0]
    if raw is None:
        return default
    return max(lo, min(hi, int(raw)))


class Handler(BaseHTTPRequestHandler):
    storage_conn = None   # 由 serve() 注入
    static_files = {}     # 由 serve() 注入：进程启动时一次性读入内存（chart.umd.js ~200KB 不再每请求读盘）

    def _host_ok(self):
        """Host 校验（防 DNS rebinding）：GET/POST 共用，远端域名直连一律 403。"""
        return self.headers.get("Host", "").split(":")[0] in _ALLOWED_HOSTS

    def _origin_ok(self):
        """Origin 精确比较 hostname（startswith 会被 http://127.0.0.1.evil.com 绕过）；端口不强制。"""
        origin = self.headers.get("Origin", "")
        if not origin:
            return True
        return (urllib.parse.urlparse(origin).hostname or "") in _ALLOWED_HOSTS

    def do_GET(self):
        if not self._host_ok():
            return self._send(403, "text/plain", b"forbidden host")
        parsed = urllib.parse.urlparse(self.path)
        q = urllib.parse.parse_qs(parsed.query)
        try:
            if parsed.path == "/":
                return self._file("index.html", cache="no-store")   # HTML 自身不缓存（?v= 救不了它）
            if parsed.path in ("/chart.umd.js", "/panel.css", "/panel.js", "/favicon.svg"):   # 字面白名单
                return self._file(parsed.path.lstrip("/"))
            if parsed.path == "/favicon.ico":
                return self._send(204, "text/plain", b"")
            if parsed.path == "/api/throttle":
                from zhulong import throttle as _th
                return self._send(200, "application/json; charset=utf-8",
                                  json.dumps({"enabled": storage.kv_get(self.storage_conn, "throttle_enabled") or "default",
                                              "rules": _th.rules(config),
                                              "applied": storage.kv_get(self.storage_conn, "throttle_applied") or "{}"},
                                             ensure_ascii=False).encode("utf-8"))
            if parsed.path == "/api/action_token":
                # 动作令牌：同源页面才可读（SOP 天然防线），POST 必须携带
                return self._send(200, "application/json; charset=utf-8",
                                  json.dumps({"token": ACTION_TOKEN}).encode("utf-8"))
            if parsed.path == "/api/current":
                out = {}
                labels = {}
                for c, k in _CURRENT_KEYS:
                    row = storage.latest(self.storage_conn, c, k)
                    if row:
                        out[k] = row[1]
                        if row[2]:
                            labels[k] = row[2]   # 进程名/最忙的盘等标签
                names = {}
                for c, k in _CURRENT_KEYS:
                    if k in config.DISPLAY_NAMES:
                        names[k] = list(config.DISPLAY_NAMES[k])
                for w in config.WATCH_PROCESSES:
                    key = f"{w['name'].rsplit('.', 1)[0]}_commit_gb"
                    row = storage.latest(self.storage_conn, "process", key)
                    if row:
                        out[key] = row[1]
                    names[key] = [w.get("display_name", w["name"]),
                                  f"阈值 {w['max_commit_gb']}GB，超限自动结束（白名单进程）"]
                try:
                    from zhulong.core import anomaly
                    anomalies = anomaly.detect(anomaly.series_from_db(self.storage_conn))
                except Exception:
                    anomalies = []
                # 温和处置状态（v1.6）：面板卡片展示与开关回显
                mt = getattr(config, "MEM_TRIM", {})
                ac_kv = storage.kv_get(self.storage_conn, "autoclean_enabled")
                autoclean = {
                    "enabled": (ac_kv == "1") if ac_kv is not None else bool(mt.get("enabled")),
                    "mem_trim": [w["name"] for w in mt.get("processes", [])],
                    "restart_services": [w.get("name") for w in getattr(config, "RESTART_SERVICES", [])],
                }
                out["_meta"] = {"program_path": str(config.PROJECT_DIR), "names": names,
                                "labels": labels, "anomalies": anomalies, "autoclean": autoclean}
                return self._send(200, "application/json; charset=utf-8",
                                  json.dumps(out, ensure_ascii=False).encode("utf-8"))
            if parsed.path == "/api/history":
                try:
                    hours = _clamp_int(q, "hours", 24, 1, 744)
                    bucket = _clamp_int(q, "bucket", 300, 60, 86400)
                except ValueError:
                    return self._send(400, "text/plain", b"bad param")
                pts = storage.query_history(self.storage_conn,
                                            q.get("collector", ["memory"])[0],
                                            q.get("key", ["commit_percent"])[0],
                                            hours=hours, bucket_s=bucket)
                return self._send(200, "application/json; charset=utf-8",
                                  json.dumps({"points": pts}, ensure_ascii=False).encode("utf-8"))
            if parsed.path == "/api/events":
                try:
                    limit = _clamp_int(q, "limit", 50, 1, 500)
                    hours = _clamp_int(q, "hours", 0, 1, 744) if q.get("hours") else None
                except ValueError:
                    return self._send(400, "text/plain", b"bad param")
                evts = storage.query_events(self.storage_conn, limit=limit, hours=hours)
                return self._send(200, "application/json; charset=utf-8",
                                  json.dumps({"events": evts}, ensure_ascii=False).encode("utf-8"))
            self._send(404, "text/plain", b"not found")
        except Exception:
            self._send(500, "text/plain", b"internal error")

    def do_POST(self):
        parsed = urllib.parse.urlparse(self.path)
        if parsed.path != "/api/action":
            return self._send(405, "text/plain", b"read-only")
        # 三层防护：Host/Origin 校验 → CSRF token → 动作白名单
        if not self._host_ok():
            return self._send(403, "text/plain", b"forbidden host")
        if not self._origin_ok():
            return self._send(403, "text/plain", b"forbidden origin")
        try:
            body = json.loads(self.rfile.read(int(self.headers.get("Content-Length", 0))) or b"{}")
        except Exception:
            return self._send(400, "text/plain", b"bad json")
        if not isinstance(body, dict):
            return self._send(400, "text/plain", b"bad json")   # 123 / [...] 等非对象 JSON
        if body.get("token") != ACTION_TOKEN:
            return self._send(403, "application/json; charset=utf-8",
                                  json.dumps({"status": "forbidden"}).encode("utf-8"))
        action, target = body.get("action"), body.get("target", "")
        try:
            if action == "kill_process":
                # AUTOKILL 元素允许字符串或 dict（{"name", "path_contains"?}），白名单比对只取名字
                allowed = {w["name"] for w in config.WATCH_PROCESSES} | {
                    (e["name"] if isinstance(e, dict) else e) for e in getattr(config, "AUTOKILL", [])}
                if target not in allowed:
                    return self._send(400, "application/json; charset=utf-8",
                                      json.dumps({"status": "skipped:not_allowed"}).encode("utf-8"))
                from zhulong.actions import kill_process
                # 熔断计数走 kv 表：与自动通道共用同一份（面板手动 kill 不再绕过熔断）
                result = kill_process.run(target, config, {},
                                          kv_get=lambda k: storage.kv_get(self.storage_conn, k),
                                          kv_set=lambda k, v: storage.kv_set(self.storage_conn, k, v))
                storage.insert_event(self.storage_conn, time.strftime("%Y-%m-%dT%H:%M:%S"),
                                     "panel_action", "kill_process",
                                     json.dumps({"target": target, "result": result}, ensure_ascii=False))
                return self._send(200, "application/json; charset=utf-8",
                                  json.dumps(result, ensure_ascii=False).encode("utf-8"))
            if action in ("throttle_on", "throttle_off"):
                storage.kv_set(self.storage_conn, "throttle_enabled", "1" if action == "throttle_on" else "0")
                return self._send(200, "application/json; charset=utf-8",
                                  json.dumps({"status": "ok", "enabled": action == "throttle_on"}).encode("utf-8"))
            if action == "mem_trim":
                # 目标必须在 MEM_TRIM 名单内（镜像 kill 的服务端名单比对）
                allowed = {w["name"] for w in getattr(config, "MEM_TRIM", {}).get("processes", [])}
                if target not in allowed:
                    return self._send(400, "application/json; charset=utf-8",
                                      json.dumps({"status": "skipped:not_allowed"}).encode("utf-8"))
                from zhulong import memtrim
                # 与自动巡检共用同一条安全链与 kv 熔断计数（面板手动不绕过熔断）
                result = memtrim.trim_once(target, config, {},
                                           kv_get=lambda k: storage.kv_get(self.storage_conn, k),
                                           kv_set=lambda k, v: storage.kv_set(self.storage_conn, k, v))
                storage.insert_event(self.storage_conn, time.strftime("%Y-%m-%dT%H:%M:%S"),
                                     "panel_action", "mem_trim",
                                     json.dumps({"target": target, "result": result}, ensure_ascii=False))
                return self._send(200, "application/json; charset=utf-8",
                                  json.dumps(result, ensure_ascii=False).encode("utf-8"))
            if action == "restart_service":
                allowed = {w["name"] for w in getattr(config, "RESTART_SERVICES", [])}
                if target not in allowed:
                    return self._send(400, "application/json; charset=utf-8",
                                      json.dumps({"status": "skipped:not_allowed"}).encode("utf-8"))
                from zhulong.actions import restart_service
                result = restart_service.run({"service": target}, config, {},
                                             kv_get=lambda k: storage.kv_get(self.storage_conn, k),
                                             kv_set=lambda k, v: storage.kv_set(self.storage_conn, k, v))
                storage.insert_event(self.storage_conn, time.strftime("%Y-%m-%dT%H:%M:%S"),
                                     "panel_action", "restart_service",
                                     json.dumps({"target": target, "result": result}, ensure_ascii=False))
                return self._send(200, "application/json; charset=utf-8",
                                  json.dumps(result, ensure_ascii=False).encode("utf-8"))
            if action in ("autoclean_on", "autoclean_off"):
                # 温和处置总开关（只闸自动通道；面板手动动作不受限）
                storage.kv_set(self.storage_conn, "autoclean_enabled",
                               "1" if action == "autoclean_on" else "0")
                return self._send(200, "application/json; charset=utf-8",
                                  json.dumps({"status": "ok", "enabled": action == "autoclean_on"}).encode("utf-8"))
            return self._send(400, "application/json; charset=utf-8",
                              json.dumps({"status": "unknown_action"}).encode("utf-8"))
        except Exception:
            # 与 do_GET 同风格：sqlite 等内部错误回 500，不断连无响应
            LOG.exception("动作通道处理异常")
            return self._send(500, "application/json; charset=utf-8",
                              json.dumps({"status": "internal_error"}).encode("utf-8"))

    def _file(self, name, cache="max-age=3600"):
        body = self.static_files.get(name)
        if body is None:
            return self._send(404, "text/plain", b"not found")
        # 静态资源可缓存 1 小时；前端更新靠引用 URL 的 ?v= 版本串刷新。
        # index.html 自身必须 no-store——?v= 救不了 HTML 自己（调用方传 cache 覆盖）
        self._send(200, _MIME[Path(name).suffix], body, cache=cache)

    def _send(self, code, ctype, body, cache="no-store"):
        try:
            self.send_response(code)
            self.send_header("Content-Type", ctype)
            self.send_header("Cache-Control", cache)   # 默认 no-store：/api/* 与错误响应不缓存
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            if body:
                self.wfile.write(body)
        except (ConnectionResetError, BrokenPipeError, ConnectionAbortedError):
            pass   # 浏览器中断连接（WinError 10054）是常态，静默

    def handle_error(self, request, client_address):
        """浏览器中途断连不刷 traceback；其余异常记一行（不带栈）。"""
        import sys
        exc = sys.exc_info()[1]
        if isinstance(exc, (ConnectionResetError, BrokenPipeError, ConnectionAbortedError)):
            return
        LOG.warning("面板请求处理异常：%r", exc)

    def log_message(self, *args):
        pass   # 面板访问不刷日志


def serve(conn, port=None):
    Handler.storage_conn = conn
    Handler.static_files = {n: (STATIC / n).read_bytes() for n in _STATIC_NAMES}
    # port=0 必须生效（测试用临时端口），不能用 `port or config.PORT`
    return ThreadingHTTPServer((config.HOST, config.PORT if port is None else port), Handler)

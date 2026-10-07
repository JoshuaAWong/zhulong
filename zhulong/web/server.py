"""只读面板服务 + 令牌守卫的动作通道。
红线修订：面板仍零"配置写"端点；仅 POST /api/action 一个动作端点——
CSRF token（同源页面才可读）+ Host/Origin 校验（防 DNS rebinding）+ 动作白名单 + kill 七步安全链。"""
import json
import secrets
import time
import urllib.parse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

from zhulong import config
from zhulong.core import storage

ACTION_TOKEN = secrets.token_hex(16)   # 进程级一次性动作令牌（GET /api/action_token 仅同源可读）

STATIC = Path(__file__).parent / "static"
_MIME = {".html": "text/html; charset=utf-8", ".js": "text/javascript; charset=utf-8",
         ".css": "text/css; charset=utf-8", ".svg": "image/svg+xml"}
_CURRENT_KEYS = [("memory", "commit_percent"), ("memory", "commit_used_gb"),
                 ("memory", "commit_limit_gb"), ("memory", "mem_percent"),
                 ("memory", "mem_used_gb"), ("memory", "mem_total_gb"),
                 ("memory", "pagefile_used_gb"), ("memory", "pagefile_total_gb"),
                 ("cpu", "cpu_percent"), ("cpu", "cpu_max_core"),
                 ("diskio", "io_read_mb_s"), ("diskio", "io_write_mb_s"),
                 ("net", "net_down_mb_s"), ("net", "net_up_mb_s"),
                 ("net", "ping_gw_ms"), ("net", "ping_net_ms"), ("net", "ping_jitter_ms"),
                 ("gpu", "gpu_temp"), ("gpu", "gpu_util"), ("gpu", "gpu_mem_used"), ("gpu", "gpu_mem_total"),
                 ("proctop", "cpu_top_pct"), ("proctop", "io_top_mb"),
                 ("proctop", "proctop1"), ("proctop", "proctop2"), ("proctop", "proctop3"),
                 ("proctop", "proctop4"), ("proctop", "proctop5")]


class Handler(BaseHTTPRequestHandler):
    storage_conn = None   # 由 serve() 注入

    def do_GET(self):
        parsed = urllib.parse.urlparse(self.path)
        q = urllib.parse.parse_qs(parsed.query)
        try:
            if parsed.path == "/":
                return self._file("index.html")
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
                out["_meta"] = {"program_path": str(config.PROJECT_DIR), "names": names,
                                "labels": labels, "anomalies": anomalies}
                return self._send(200, "application/json; charset=utf-8",
                                  json.dumps(out, ensure_ascii=False).encode("utf-8"))
            if parsed.path == "/api/history":
                pts = storage.query_history(self.storage_conn,
                                            q.get("collector", ["memory"])[0],
                                            q.get("key", ["commit_percent"])[0],
                                            hours=int(q.get("hours", ["24"])[0]),
                                            bucket_s=int(q.get("bucket", ["300"])[0]))
                return self._send(200, "application/json; charset=utf-8",
                                  json.dumps({"points": pts}, ensure_ascii=False).encode("utf-8"))
            if parsed.path == "/api/events":
                hours_q = q.get("hours", [None])[0]
                evts = storage.query_events(self.storage_conn,
                                            limit=int(q.get("limit", ["50"])[0]),
                                            hours=int(hours_q) if hours_q else None)
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
        if self.headers.get("Host", "").split(":")[0] not in ("127.0.0.1", "localhost"):
            return self._send(403, "text/plain", b"forbidden host")
        origin = self.headers.get("Origin", "")
        if origin and not (origin.startswith("http://127.0.0.1") or origin.startswith("http://localhost")):
            return self._send(403, "text/plain", b"forbidden origin")
        try:
            body = json.loads(self.rfile.read(int(self.headers.get("Content-Length", 0))) or b"{}")
        except Exception:
            return self._send(400, "text/plain", b"bad json")
        if body.get("token") != ACTION_TOKEN:
            return self._send(403, "application/json; charset=utf-8",
                                  json.dumps({"status": "forbidden"}).encode("utf-8"))
        action, target = body.get("action"), body.get("target", "")
        if action == "kill_process":
            allowed = {w["name"] for w in config.WATCH_PROCESSES} | set(getattr(config, "AUTOKILL", []))
            if target not in allowed:
                return self._send(400, "application/json; charset=utf-8",
                                      json.dumps({"status": "skipped:not_allowed"}).encode("utf-8"))
            from zhulong.actions import kill_process
            result = kill_process.run(target, config, {})
            storage.insert_event(self.storage_conn, time.strftime("%Y-%m-%dT%H:%M:%S"),
                                 "panel_action", "kill_process",
                                 json.dumps({"target": target, "result": result}, ensure_ascii=False))
            return self._send(200, "application/json; charset=utf-8",
                              json.dumps(result, ensure_ascii=False).encode("utf-8"))
        if action in ("throttle_on", "throttle_off"):
            storage.kv_set(self.storage_conn, "throttle_enabled", "1" if action == "throttle_on" else "0")
            return self._send(200, "application/json; charset=utf-8",
                              json.dumps({"status": "ok", "enabled": action == "throttle_on"}).encode("utf-8"))
        return self._send(400, "application/json; charset=utf-8",
                          json.dumps({"status": "unknown_action"}).encode("utf-8"))

    def _file(self, name):
        p = STATIC / name
        body = p.read_bytes()
        self.send_response(200)
        self.send_header("Content-Type", _MIME[p.suffix])
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _send(self, code, ctype, body):
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        if body:
            self.wfile.write(body)

    def log_message(self, *args):
        pass   # 面板访问不刷日志


def serve(conn, port=None):
    Handler.storage_conn = conn
    # port=0 必须生效（测试用临时端口），不能用 `port or config.PORT`
    return ThreadingHTTPServer((config.HOST, config.PORT if port is None else port), Handler)

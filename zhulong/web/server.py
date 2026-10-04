"""只读面板服务：GET-only，绑 127.0.0.1。零 POST/执行端点是安全红线（防 DNS rebinding）。"""
import json
import urllib.parse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

from zhulong import config
from zhulong.core import storage

STATIC = Path(__file__).parent / "static"
_MIME = {".html": "text/html; charset=utf-8", ".js": "text/javascript; charset=utf-8",
         ".css": "text/css; charset=utf-8"}
_CURRENT_KEYS = [("memory", "commit_percent"), ("memory", "commit_used_gb"),
                 ("memory", "commit_limit_gb"), ("memory", "mem_percent"),
                 ("memory", "mem_used_gb"), ("memory", "mem_total_gb"),
                 ("memory", "pagefile_used_gb"), ("memory", "pagefile_total_gb")]


class Handler(BaseHTTPRequestHandler):
    storage_conn = None   # 由 serve() 注入

    def do_GET(self):
        parsed = urllib.parse.urlparse(self.path)
        q = urllib.parse.parse_qs(parsed.query)
        try:
            if parsed.path == "/":
                return self._file("index.html")
            if parsed.path in ("/chart.umd.js", "/panel.css", "/panel.js"):   # 字面白名单
                return self._file(parsed.path.lstrip("/"))
            if parsed.path == "/favicon.ico":
                return self._send(204, "text/plain", b"")
            if parsed.path == "/api/current":
                out = {}
                for c, k in _CURRENT_KEYS:
                    row = storage.latest(self.storage_conn, c, k)
                    if row:
                        out[k] = row[1]
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
                out["_meta"] = {"program_path": str(config.PROJECT_DIR), "names": names}
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
        self._send(405, "text/plain", b"read-only")   # 安全红线

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

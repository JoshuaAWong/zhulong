import http.client
import json
import tempfile
import threading
from pathlib import Path

from zhulong.core import storage
from zhulong.web import server


def get(conn_host, port, path, method="GET"):
    c = http.client.HTTPConnection(conn_host, port, timeout=5)
    c.request(method, path)
    r = c.getresponse()
    body = r.read()
    c.close()
    return r.status, body


def main():
    with tempfile.TemporaryDirectory() as td:
        conn = storage.connect(Path(td) / "t.db")
        storage.init_schema(conn)
        storage.insert_metrics(conn, [("2026-10-02T10:00:00", "memory", "commit_percent", 66.0, "")])
        srv = server.serve(conn, port=0)
        port = srv.server_address[1]
        threading.Thread(target=srv.serve_forever, daemon=True).start()
        try:
            st, body = get("127.0.0.1", port, "/")
            assert st == 200 and "烛龙".encode("utf-8") in body, st
            st, body = get("127.0.0.1", port, "/api/current")
            data = json.loads(body)
            assert st == 200 and data["commit_percent"] == 66.0, data
            # _meta：路径与中文映射
            meta = data["_meta"]
            assert meta["program_path"] and "烛龙" in meta["program_path"], meta
            assert meta["names"]["commit_percent"][0] == "提交内存水位", meta
            st, body = get("127.0.0.1", port, "/api/history?collector=memory&key=commit_percent&hours=24")
            assert st == 200 and "points" in json.loads(body)
            # 区间三档：hours+bucket 参数透传（30 天档也应 200）
            st, body = get("127.0.0.1", port, "/api/history?collector=memory&key=commit_percent&hours=720&bucket=7200")
            assert st == 200 and "points" in json.loads(body)
            st, body = get("127.0.0.1", port, "/api/events?limit=10")
            assert st == 200 and "events" in json.loads(body)
            st, body = get("127.0.0.1", port, "/api/events?hours=1&limit=10")
            assert st == 200 and "events" in json.loads(body)
            st, _ = get("127.0.0.1", port, "/api/current", method="POST")
            assert st == 405, st   # 安全红线：写请求必须拒绝
            st, _ = get("127.0.0.1", port, "/favicon.ico")
            assert st == 204
            # 静态拆分文件：字面白名单路由 + MIME 硬编码
            st, body = get("127.0.0.1", port, "/panel.css")
            assert st == 200, st
            st, body = get("127.0.0.1", port, "/panel.js")
            assert st == 200 and b"refresh" in body, st
        finally:
            srv.shutdown()
        conn.close()
    print("PASS")


if __name__ == "__main__":
    main()

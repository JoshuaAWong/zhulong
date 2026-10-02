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
            st, body = get("127.0.0.1", port, "/api/history?collector=memory&key=commit_percent&hours=24")
            assert st == 200 and "points" in json.loads(body)
            st, body = get("127.0.0.1", port, "/api/events?limit=10")
            assert st == 200 and "events" in json.loads(body)
            st, _ = get("127.0.0.1", port, "/api/current", method="POST")
            assert st == 405, st   # 安全红线：写请求必须拒绝
            st, _ = get("127.0.0.1", port, "/favicon.ico")
            assert st == 204
        finally:
            srv.shutdown()
        conn.close()
    print("PASS")


if __name__ == "__main__":
    main()

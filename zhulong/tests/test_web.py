import http.client
import json
import tempfile
import threading
import time
from pathlib import Path

from zhulong.core import storage
from zhulong.web import server


def get(conn_host, port, path, method="GET", host=None, origin=None):
    """host/origin 传 None 走 http.client 默认 Host（127.0.0.1:port）；传串则覆盖（测恶意 Host）。"""
    c = http.client.HTTPConnection(conn_host, port, timeout=5)
    c.putrequest(method, path, skip_host=host is not None)
    if host is not None:
        c.putheader("Host", host)
    if origin is not None:
        c.putheader("Origin", origin)
    c.endheaders()
    r = c.getresponse()
    body = r.read()
    headers = dict(r.getheaders())
    c.close()
    return r.status, headers, body


def post_json(port, path, obj, origin=None):
    c = http.client.HTTPConnection("127.0.0.1", port, timeout=5)
    headers = {"Content-Type": "application/json"}
    if origin is not None:
        headers["Origin"] = origin
    c.request("POST", path, body=json.dumps(obj).encode("utf-8"), headers=headers)
    r = c.getresponse()
    body = r.read()
    c.close()
    return r.status, body


def main():
    with tempfile.TemporaryDirectory() as td:
        conn = storage.connect(Path(td) / "t.db")
        storage.init_schema(conn)
        storage.insert_metrics(conn, [(time.strftime("%Y-%m-%dT%H:%M:%S"),
                                       "memory", "commit_percent", 66.0, "")])
        srv = server.serve(conn, port=0)
        port = srv.server_address[1]
        threading.Thread(target=srv.serve_forever, daemon=True).start()
        try:
            st, hdr, body = get("127.0.0.1", port, "/")
            assert st == 200 and "烛龙".encode("utf-8") in body, st
            assert hdr.get("Cache-Control") == "no-store", hdr   # 终审修订：HTML 自身不缓存（?v= 救不了它）
            st, hdr, body = get("127.0.0.1", port, "/api/current")
            data = json.loads(body)
            assert st == 200 and data["commit_percent"] == 66.0, data
            assert hdr.get("Cache-Control") == "no-store", hdr   # /api/* 不缓存
            # _meta：路径与中文映射
            meta = data["_meta"]
            assert meta["program_path"] and "烛龙" in meta["program_path"], meta
            assert meta["names"]["commit_percent"][0] == "提交内存水位", meta
            st, _, body = get("127.0.0.1", port, "/api/history?collector=memory&key=commit_percent&hours=24")
            assert st == 200 and "points" in json.loads(body)
            # 区间三档：hours+bucket 参数透传（30 天档也应 200）
            st, _, body = get("127.0.0.1", port, "/api/history?collector=memory&key=commit_percent&hours=720&bucket=7200")
            assert st == 200 and "points" in json.loads(body)
            st, _, body = get("127.0.0.1", port, "/api/events?limit=10")
            assert st == 200 and "events" in json.loads(body)
            st, _, body = get("127.0.0.1", port, "/api/events?hours=1&limit=10")
            assert st == 200 and "events" in json.loads(body)
            st, _, _ = get("127.0.0.1", port, "/api/current", method="POST")
            assert st == 405, st   # 安全红线：非动作端点的写请求必须拒绝

            # ---- B2：Host 校验覆盖 GET 系端点（DNS rebinding 不得读到令牌/数据）----
            st, _, _ = get("127.0.0.1", port, "/api/action_token", host="evil.com")
            assert st == 403, st
            st, _, _ = get("127.0.0.1", port, "/api/current", host="127.0.0.1.evil.com")
            assert st == 403, st
            st, _, _ = get("127.0.0.1", port, "/", host="evil.com")
            assert st == 403, st
            st, _, _ = get("127.0.0.1", port, "/api/current", host="localhost:8890")
            assert st == 200, st   # 合法 localhost 放行

            # ---- B2：GET 参数钳制与 400 ----
            st, _, _ = get("127.0.0.1", port, "/api/history?hours=abc")
            assert st == 400, st   # 解析失败 → 400（不再 500）
            st, _, _ = get("127.0.0.1", port, "/api/history?hours=99999")
            assert st == 200, st   # 钳到 744 而非报错
            st, _, _ = get("127.0.0.1", port, "/api/events?limit=-5")
            assert st == 200, st   # 负数钳到合法值
            assert server._clamp_int({}, "hours", 24, 1, 744) == 24
            assert server._clamp_int({"hours": ["99999"]}, "hours", 24, 1, 744) == 744
            assert server._clamp_int({"hours": ["-5"]}, "hours", 24, 1, 744) == 1
            try:
                server._clamp_int({"hours": ["abc"]}, "hours", 24, 1, 744)
                raise SystemExit("hours=abc 应抛 ValueError")
            except ValueError:
                pass

            # 动作通道：令牌门禁
            st, _, body = get("127.0.0.1", port, "/api/action_token")
            tok = json.loads(body)["token"]
            assert st == 200 and tok, body
            st, _, body = get("127.0.0.1", port, "/api/action", method="POST")
            assert st in (400, 403), st   # 无 token 必须拒
            st, body = post_json(port, "/api/action", {"action": "kill_process", "target": "HYPHelper.exe", "token": "wrong"})
            assert st == 403 and json.loads(body)["status"] == "forbidden", body   # 错 token → 403
            # B2：跨域 Origin 精确比较（startswith 时代会被 127.0.0.1.evil.com 绕过）
            st, _ = post_json(port, "/api/action", {"action": "throttle_on", "token": tok},
                              origin="http://127.0.0.1.evil.com")
            assert st == 403, st
            st, body = post_json(port, "/api/action", {"action": "throttle_on", "token": tok},
                                 origin="http://127.0.0.1:8890")
            assert st == 200 and json.loads(body)["status"] == "ok", body   # 同源（端口不强制）放行
            post_json(port, "/api/action", {"action": "throttle_off", "token": tok})   # 还原
            # B2：非 dict JSON body → 400（不再 AttributeError 断连）
            st, _ = post_json(port, "/api/action", [1, 2, 3])
            assert st == 400, st
            st, _ = post_json(port, "/api/action", 123)
            assert st == 400, st
            st, body = post_json(port, "/api/action", {"action": "kill_process", "target": "notepad.exe", "token": tok})
            assert st == 400 and json.loads(body)["status"] == "skipped:not_allowed", body
            st, body = post_json(port, "/api/action", {"action": "kill_process", "target": "HYPHelper.exe", "token": tok})
            assert st == 200 and json.loads(body)["status"].startswith(("skipped", "killed", "degraded")), body
            # 面板手动 kill 与自动通道共用 kv 熔断计数（按进程名分桶）：kv 已有 2 次 → 第 3 次被熔断
            now = time.time()
            storage.kv_set(conn, "kill_times", json.dumps({"hyphelper.exe": [now - 60, now - 30]}))
            st, body = post_json(port, "/api/action", {"action": "kill_process", "target": "HYPHelper.exe", "token": tok})
            assert st == 200 and json.loads(body)["status"] == "degraded:frequency_cap", body
            st, body = post_json(port, "/api/action", {"action": "nope", "token": tok})
            assert st == 400 and json.loads(body)["status"] == "unknown_action", body
            # 终审修订：do_POST 内部异常（如 sqlite 抛错）回 500 JSON，不断连无响应
            orig_kv_set = storage.kv_set
            storage.kv_set = lambda *a, **k: (_ for _ in ()).throw(RuntimeError("db gone"))
            try:
                st, body = post_json(port, "/api/action", {"action": "throttle_on", "token": tok})
                assert st == 500 and json.loads(body)["status"] == "internal_error", (st, body)
            finally:
                storage.kv_set = orig_kv_set
            st, _, _ = get("127.0.0.1", port, "/favicon.ico")
            assert st == 204
            # 静态拆分文件：字面白名单路由 + MIME 硬编码 + B2 内存缓存与 Cache-Control
            st, hdr, body = get("127.0.0.1", port, "/panel.css")
            assert st == 200 and hdr.get("Cache-Control") == "max-age=3600", (st, hdr)
            st, hdr, body = get("127.0.0.1", port, "/panel.js")
            assert st == 200 and b"refresh" in body and hdr.get("Cache-Control") == "max-age=3600", (st, hdr)
            # 版本查询串不影响白名单匹配（query 不参与路由）
            st, _, body = get("127.0.0.1", port, "/panel.js?v=3")
            assert st == 200 and b"refresh" in body, st
        finally:
            srv.shutdown()
        conn.close()
    print("PASS")


if __name__ == "__main__":
    main()

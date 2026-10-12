"""泄漏服务温和重启（actions/restart_service.py）：安全链全分支 + 触发规则生成。"""
import json
import tempfile
import time
from pathlib import Path

from zhulong.actions import restart_service as rs
from zhulong.core import storage


class FakeSvc:
    """status() 依次返回 statuses 列表，剩最后一个后恒返末值（模拟轮询稳定态）。"""
    def __init__(self, statuses):
        self._statuses = list(statuses)
        self.stops = self.starts = 0

    def status(self):
        return self._statuses.pop(0) if len(self._statuses) > 1 else self._statuses[0]

    def stop(self):
        self.stops += 1

    def start(self):
        self.starts += 1


class FakePS:
    AccessDenied = type("AccessDenied", (Exception,), {})

    def __init__(self, svc=None, raise_on_get=False):
        self._svc, self._raise = svc, raise_on_get

    def win_service_get(self, name):
        if self._raise:
            raise RuntimeError("no such service")
        return self._svc


class FakeCfg:
    RESTART_SERVICES = [
        {"name": "FakeSvc", "display_name": "测试服务",
         "trigger_processes": ["WmiPrvSE.exe"], "max_restarts_per_hour": 1},
        {"name": "RpcSs", "trigger_processes": ["x.exe"], "max_restarts_per_hour": 1},
    ]


def main():
    with tempfile.TemporaryDirectory() as td:
        conn = storage.connect(Path(td) / "t.db")
        storage.init_schema(conn)
        kv_get = lambda k: storage.kv_get(conn, k)
        kv_set = lambda k, v: storage.kv_set(conn, k, v)
        # 超时压 0（_wait_status 只做终查不轮询）、重试间隔压 0，保证测试秒级完成
        orig = (rs.STOP_TIMEOUT_S, rs.START_TIMEOUT_S, rs.RETRY_WAIT_S)
        rs.STOP_TIMEOUT_S = rs.START_TIMEOUT_S = rs.RETRY_WAIT_S = 0
        try:
            # 无服务名 / 白名单外 / 关键服务硬阻断
            assert rs.run({}, FakeCfg, {}, psutil_mod=FakePS())["status"] == "skipped:no_service"
            assert rs.run({"service": "Other"}, FakeCfg, {}, psutil_mod=FakePS(),
                          kv_get=kv_get, kv_set=kv_set)["status"] == "skipped:not_whitelisted"
            r = rs.run({"service": "RpcSs"}, FakeCfg, {}, psutil_mod=FakePS(),
                       kv_get=kv_get, kv_set=kv_set)
            assert r["status"] == "blocked:never_restart", r   # 误配白名单也动不了

            # 总开关只闸自动通道：auto=True 且 kv 关 → skipped:disabled；无 auto 标记照常走
            storage.kv_set(conn, "autoclean_enabled", "0")
            r = rs.run({"service": "FakeSvc", "auto": True}, FakeCfg, {},
                       psutil_mod=FakePS(FakeSvc(["running", "stopped", "running"])),
                       kv_get=kv_get, kv_set=kv_set)
            assert r["status"] == "skipped:disabled", r
            storage.kv_set(conn, "autoclean_enabled", "1")

            # 频次熔断：窗口内已有 1 次（上限 1/小时）→ 降级
            storage.kv_set(conn, "restart_times", json.dumps({"fakesvc": [time.time() - 60]}))
            r = rs.run({"service": "FakeSvc"}, FakeCfg, {}, psutil_mod=FakePS(),
                       kv_get=kv_get, kv_set=kv_set)
            assert r["status"] == "degraded:frequency_cap", r
            storage.kv_set(conn, "restart_times", "{}")

            # 服务不存在 / 未在运行（发起即记账设计：每个走过熔断的场景都消耗额度，场景间清空）
            assert rs.run({"service": "FakeSvc"}, FakeCfg, {},
                          psutil_mod=FakePS(raise_on_get=True),
                          kv_get=kv_get, kv_set=kv_set)["status"] == "skipped:not_found"
            storage.kv_set(conn, "restart_times", "{}")
            r = rs.run({"service": "FakeSvc"}, FakeCfg, {},
                       psutil_mod=FakePS(FakeSvc(["stopped"])),
                       kv_get=kv_get, kv_set=kv_set)
            assert r["status"] == "skipped:not_running", r
            storage.kv_set(conn, "restart_times", "{}")

            # stop 超时降级（状态停在 stop_pending 不翻转）
            r = rs.run({"service": "FakeSvc"}, FakeCfg, {},
                       psutil_mod=FakePS(FakeSvc(["running", "stop_pending"])),
                       kv_get=kv_get, kv_set=kv_set)
            assert r["status"] == "degraded:stop_timeout", r
            storage.kv_set(conn, "restart_times", "{}")

            # start 失败：3 次尝试（1+2 重试）全不成功 → degraded:start_failed（补偿路径）
            svc = FakeSvc(["running", "stopped", "start_pending"])
            r = rs.run({"service": "FakeSvc"}, FakeCfg, {}, psutil_mod=FakePS(svc),
                       kv_get=kv_get, kv_set=kv_set)
            assert r["status"] == "degraded:start_failed", r
            assert svc.stops == 1 and svc.starts == 1 + rs.START_RETRIES, (svc.stops, svc.starts)
            # 发起即记账：失败也占用熔断额度（防规则冷却到期反复敲打半死服务）
            buckets = json.loads(storage.kv_get(conn, "restart_times"))
            assert len(buckets["fakesvc"]) == 1, buckets
            storage.kv_set(conn, "restart_times", "{}")

            # 正常重启：stop → stopped → start → running；记账一次
            svc = FakeSvc(["running", "stopped", "running"])
            r = rs.run({"service": "FakeSvc"}, FakeCfg, {}, psutil_mod=FakePS(svc),
                       kv_get=kv_get, kv_set=kv_set)
            assert r["status"] == "restarted" and svc.stops == 1 and svc.starts == 1, r
            buckets = json.loads(storage.kv_get(conn, "restart_times"))
            assert len(buckets["fakesvc"]) == 1, buckets
            # 熔断随即生效
            r = rs.run({"service": "FakeSvc"}, FakeCfg, {},
                       psutil_mod=FakePS(FakeSvc(["running", "stopped", "running"])),
                       kv_get=kv_get, kv_set=kv_set)
            assert r["status"] == "degraded:frequency_cap", r

            # ---- 触发规则生成（rules._restart_rules；RULES 在导入时按出厂空名单构建，
            # 故临时替换 config 再验生成器本身）----
            from zhulong import rules as rules_mod
            orig_rs = rules_mod.config.RESTART_SERVICES
            rules_mod.config.RESTART_SERVICES = FakeCfg.RESTART_SERVICES
            try:
                rr = rules_mod._restart_rules()
            finally:
                rules_mod.config.RESTART_SERVICES = orig_rs
            rr = [x for x in rr if x["name"] == "restart_FakeSvc"]
            assert len(rr) == 1, rr
            rule = rr[0]
            assert rule["params"]["service"] == "FakeSvc" and rule["params"]["auto"] is True
            assert rule["actions"] == ["restart_service", "toast"]

            class FakeCtx:
                def __init__(self, anomalies):
                    self.anomalies = anomalies
            # trigger_processes 命中异常 → 条件成立；未命中 → 不成立
            assert rule["condition"](FakeCtx([{"name": "WmiPrvSE.exe"}]))
            assert not rule["condition"](FakeCtx([{"name": "other.exe"}]))
            assert not rule["condition"](FakeCtx([]))
        finally:
            rs.STOP_TIMEOUT_S, rs.START_TIMEOUT_S, rs.RETRY_WAIT_S = orig
        conn.close()
    print("PASS")


if __name__ == "__main__":
    main()

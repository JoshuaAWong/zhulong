"""内存回收（memtrim.py）：安全链全分支 + 巡检分频/开关/压力门槛/事件去重。"""
import json
import tempfile
import time
from pathlib import Path

from zhulong import memtrim
from zhulong.core import protect, storage

GB = 1024 ** 3


class FakeMI:
    def __init__(self, wset):
        self.wset = wset


class FakeProc:
    def __init__(self, pid, name, wset_gb, age_s=3600.0, exe=r"C:\Apps\Big\app.exe",
                 username=r"DESKTOP\user", flip_create_time=False):
        self.pid = pid
        self.info = {"pid": pid, "name": name, "create_time": time.time() - age_s,
                     "memory_info": FakeMI(wset_gb * GB)}
        self._exe, self._username = exe, username
        self._flip, self._flipped = flip_create_time, False

    def exe(self):
        return self._exe

    def username(self):
        return self._username

    def create_time(self):
        if self._flip and not self._flipped:
            self._flipped = True   # 第二次读（竞态防护处）返回不同值 → 模拟 PID 复用
            return self.info["create_time"] + 999
        return self.info["create_time"]

    def memory_info(self):
        return self.info["memory_info"]


class FakePS:
    NoSuchProcess = type("NoSuchProcess", (Exception,), {})
    AccessDenied = type("AccessDenied", (Exception,), {})

    def __init__(self, procs, mem_percent=80.0):
        self._procs, self._mem = procs, mem_percent
        self.virtual_calls = 0

    def process_iter(self, attrs=None):
        return list(self._procs)

    def virtual_memory(self):
        self.virtual_calls += 1
        obj = type("VM", (), {})()
        obj.percent = self._mem
        return obj


BASE_RULES = [{"name": "BigApp.exe", "min_ws_gb": 2.0, "min_age_s": 300,
               "path_contains": "Apps", "max_trims_per_10min": 1}]


class FakeCfg:
    MEM_TRIM = {"enabled": True, "interval_cycles": 1, "min_mem_percent": 60,
                "processes": BASE_RULES}


def main():
    with tempfile.TemporaryDirectory() as td:
        conn = storage.connect(Path(td) / "t.db")
        storage.init_schema(conn)
        kv_get = lambda k: storage.kv_get(conn, k)
        kv_set = lambda k, v: storage.kv_set(conn, k, v)
        trim_calls = []
        orig_trim = memtrim._trim_handle
        memtrim._trim_handle = lambda pid: trim_calls.append(pid) or (True, 0)
        try:
            # 白名单外
            r = memtrim.trim_once("nope.exe", FakeCfg, {}, psutil_mod=FakePS([]),
                                  kv_get=kv_get, kv_set=kv_set)
            assert r["status"] == "skipped:not_whitelisted", r

            # 名单级硬阻断：误配 explorer/安全软件进白名单也动不了
            class BadCfg(FakeCfg):
                MEM_TRIM = {"processes": [{"name": "explorer.exe", "min_ws_gb": 0.1},
                                          {"name": "QQPCTray.exe", "min_ws_gb": 0.1}]}
            r = memtrim.trim_once("explorer.exe", BadCfg, {}, psutil_mod=FakePS([]),
                                  kv_get=kv_get, kv_set=kv_set)
            assert r["status"] == "blocked:never_kill", r
            r = memtrim.trim_once("QQPCTray.exe", BadCfg, {}, psutil_mod=FakePS([]),
                                  kv_get=kv_get, kv_set=kv_set)
            assert r["status"] == "blocked:safety_software", r

            # 频次熔断：kv 已有 1 次（10 分钟窗口内）→ 降级
            storage.kv_set(conn, "trim_times", json.dumps({"bigapp.exe": [time.time() - 60]}))
            r = memtrim.trim_once("BigApp.exe", FakeCfg, {}, psutil_mod=FakePS([]),
                                  kv_get=kv_get, kv_set=kv_set)
            assert r["status"] == "degraded:frequency_cap", r
            storage.kv_set(conn, "trim_times", "{}")

            # 进程不存在 / 低于阈值
            r = memtrim.trim_once("BigApp.exe", FakeCfg, {}, psutil_mod=FakePS([]),
                                  kv_get=kv_get, kv_set=kv_set)
            assert r["status"] == "skipped:process_gone", r
            r = memtrim.trim_once("BigApp.exe", FakeCfg, {},
                                  psutil_mod=FakePS([FakeProc(1, "BigApp.exe", 1.0)]),
                                  kv_get=kv_get, kv_set=kv_set)
            assert r["status"] == "skipped:ws_below_threshold", r

            # 关键进程识别：SYSTEM 账户 / 前台窗口 → 阻断（多重信号核心诉求）
            r = memtrim.trim_once("BigApp.exe", FakeCfg, {},
                                  psutil_mod=FakePS([FakeProc(1, "BigApp.exe", 3.0,
                                                              username=r"NT AUTHORITY\SYSTEM")]),
                                  kv_get=kv_get, kv_set=kv_set)
            assert r["status"] == "blocked:system_account", r
            orig_fg = protect.foreground_pid
            protect.foreground_pid = lambda: 1
            try:
                r = memtrim.trim_once("BigApp.exe", FakeCfg, {},
                                      psutil_mod=FakePS([FakeProc(1, "BigApp.exe", 3.0)]),
                                      kv_get=kv_get, kv_set=kv_set)
                assert r["status"] == "blocked:foreground", r
            finally:
                protect.foreground_pid = orig_fg

            # 存活不足 / 路径不匹配 / PID 复用
            r = memtrim.trim_once("BigApp.exe", FakeCfg, {},
                                  psutil_mod=FakePS([FakeProc(1, "BigApp.exe", 3.0, age_s=10)]),
                                  kv_get=kv_get, kv_set=kv_set)
            assert r["status"] == "skipped:too_young", r
            r = memtrim.trim_once("BigApp.exe", FakeCfg, {},
                                  psutil_mod=FakePS([FakeProc(1, "BigApp.exe", 3.0,
                                                              exe=r"C:\Other\BigApp.exe")]),
                                  kv_get=kv_get, kv_set=kv_set)
            assert r["status"] == "skipped:path_mismatch", r
            r = memtrim.trim_once("BigApp.exe", FakeCfg, {},
                                  psutil_mod=FakePS([FakeProc(1, "BigApp.exe", 3.0,
                                                              flip_create_time=True)]),
                                  kv_get=kv_get, kv_set=kv_set)
            assert r["status"] == "skipped:pid_reused", r

            # 成功路径：trim 记账 + 前后 ws 落结果 + 熔断随即生效
            r = memtrim.trim_once("BigApp.exe", FakeCfg, {},
                                  psutil_mod=FakePS([FakeProc(1, "BigApp.exe", 3.0)]),
                                  kv_get=kv_get, kv_set=kv_set)
            assert r["status"] == "trimmed" and r["pid"] == 1, r
            assert r["ws_gb_before"] == 3.0 and r["ws_gb_after"] == 3.0, r
            assert trim_calls == [1], trim_calls
            buckets = json.loads(storage.kv_get(conn, "trim_times"))
            assert len(buckets["bigapp.exe"]) == 1, buckets
            r = memtrim.trim_once("BigApp.exe", FakeCfg, {},
                                  psutil_mod=FakePS([FakeProc(1, "BigApp.exe", 3.0)]),
                                  kv_get=kv_get, kv_set=kv_set)
            assert r["status"] == "degraded:frequency_cap", r
            storage.kv_set(conn, "trim_times", "{}")
            trim_calls.clear()

            # ---- 巡检 apply_if_due ----
            # 分频：interval_cycles=2 时第 1 周期不动（virtual_memory 都不该被调）
            class Cfg2(FakeCfg):
                MEM_TRIM = {"enabled": True, "interval_cycles": 2, "min_mem_percent": 60,
                            "processes": BASE_RULES}
            pm = FakePS([FakeProc(1, "BigApp.exe", 3.0)])
            memtrim.apply_if_due(conn, Cfg2, {}, psutil_mod=pm)
            assert pm.virtual_calls == 0, "分频周期未到不应巡检"
            # 开关关：不动
            storage.kv_set(conn, "autoclean_enabled", "0")
            memtrim.apply_if_due(conn, FakeCfg, {}, psutil_mod=pm)
            assert pm.virtual_calls == 0, "总开关关闭不应巡检"
            storage.kv_set(conn, "autoclean_enabled", "1")
            # 压力门槛低：整轮跳过
            pm_low = FakePS([FakeProc(1, "BigApp.exe", 3.0)], mem_percent=40)
            memtrim.apply_if_due(conn, FakeCfg, {}, psutil_mod=pm_low)
            assert pm_low.virtual_calls == 1 and not trim_calls, "内存无压力不应 trim"
            # 正常巡检：trim 成功 + 事件落库（query_events 按时间倒序，evs[0] 为最新）
            n_ev = len(storage.query_events(conn, limit=100))
            memtrim.apply_if_due(conn, FakeCfg, {}, psutil_mod=pm)
            assert trim_calls == [1], trim_calls
            evs = storage.query_events(conn, limit=100)
            assert len(evs) == n_ev + 1 and evs[0][1] == "memtrim", evs
            assert "trimmed" in evs[0][3], evs[0]
            # 事件去重：同状态 skip 不重复记（B1 告警风暴教训；去重状态在 state 里，须复用同一 dict）
            storage.kv_set(conn, "trim_times", "{}")
            pm_small = FakePS([FakeProc(2, "BigApp.exe", 1.0)])   # 低于阈值 → ws_below_threshold
            n_ev = len(storage.query_events(conn, limit=100))
            st_dedup = {}
            memtrim.apply_if_due(conn, FakeCfg, st_dedup, psutil_mod=pm_small)
            memtrim.apply_if_due(conn, FakeCfg, st_dedup, psutil_mod=pm_small)
            evs = storage.query_events(conn, limit=100)
            assert len(evs) == n_ev + 1, "同状态 skip 重复记事件"
            assert "ws_below_threshold" in evs[0][3], evs[0]
        finally:
            memtrim._trim_handle = orig_trim
        conn.close()
    print("PASS")


if __name__ == "__main__":
    main()

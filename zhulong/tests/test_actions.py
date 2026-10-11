from zhulong.actions import toast, kill_process
from zhulong import config
import time


class FakeInfo(dict):
    pass


class FakeProc:
    def __init__(self, pid, name, commit_bytes, create_time, exe):
        self._pid, self._name = pid, name
        self.info = {"pid": pid, "name": name,
                     "memory_info": type("MI", (), {"commit": commit_bytes, "rss": commit_bytes})(),
                     "create_time": create_time}
        self._exe = exe
        self.killed = False

    def exe(self):
        return self._exe

    def create_time(self):
        return self.info["create_time"]

    def kill(self):
        self.killed = True


class FakePS:
    AccessDenied = type("AccessDenied", (Exception,), {})
    NoSuchProcess = type("NoSuchProcess", (Exception,), {})

    def __init__(self, procs):
        self._procs = procs

    def process_iter(self, attrs=None):
        return list(self._procs)


def cfg_with(**over):
    w = {"name": "HYPHelper.exe", "max_commit_gb": 15, "min_age_s": 60,
         "max_kills_per_10min": 2, "path_contains": "miHoYo Launcher"}
    w.update(over)
    class C: WATCH_PROCESSES = [w]
    return C


def main():
    # ---- toast 降级链：第一个失败第二个成功 ----
    calls = []
    def bad(*a, **kw): raise RuntimeError("x")
    def ok(*a, **kw): calls.append(a); return True
    r = toast.run("t", "b", chain=[bad, ok])
    assert r["status"] == "notified" and r["via"] == 1, r
    r = toast.run("t", "b", chain=[bad, bad])
    assert r["status"] == "failed", r

    # ---- kill 安全链 ----
    cfg = cfg_with()
    young = time.time() - 10          # 存活 10s < min_age_s
    old = time.time() - 3600
    good_exe = r"D:\\Game\\miHoYo Launcher\\HYPHelper.exe"
    bad_exe = r"C:\\evil\\HYPHelper.exe"

    # 1) 重读水位不足 → skip
    p = FakeProc(1, "HYPHelper.exe", 5 * 2**30, old, good_exe)
    r = kill_process.run("HYPHelper.exe", cfg, {}, psutil_mod=FakePS([p]))
    assert r["status"].startswith("skipped") and "commit" in r["status"], r
    assert not p.killed

    # 2) 进程太年轻 → skip
    p = FakeProc(2, "HYPHelper.exe", 16 * 2**30, young, good_exe)
    r = kill_process.run("HYPHelper.exe", cfg, {}, psutil_mod=FakePS([p]))
    assert r["status"] == "skipped:too_young", r

    # 3) 路径不匹配 → skip
    p = FakeProc(3, "HYPHelper.exe", 16 * 2**30, old, bad_exe)
    r = kill_process.run("HYPHelper.exe", cfg, {}, psutil_mod=FakePS([p]))
    assert r["status"] == "skipped:path_mismatch", r

    # 4) 全部通过 → killed
    state = {}
    p = FakeProc(4, "HYPHelper.exe", 16 * 2**30, old, good_exe)
    r = kill_process.run("HYPHelper.exe", cfg, state, psutil_mod=FakePS([p]))
    assert r["status"] == "killed" and p.killed and r["pid"] == 4, r

    # 5) 频次熔断：10 分钟内第 3 次 → degraded（按进程名分桶）
    now = time.time()
    state = {"kill_times": {"hyphelper.exe": [now - 60, now - 30]}}
    p = FakeProc(5, "HYPHelper.exe", 16 * 2**30, old, good_exe)
    r = kill_process.run("HYPHelper.exe", cfg, state, psutil_mod=FakePS([p]))
    assert r["status"] == "degraded:frequency_cap" and not p.killed, r

    # 6) AccessDenied → degraded 且不抛异常
    class DenyProc(FakeProc):
        def kill(self): raise FakePS.AccessDenied("denied")
    p = DenyProc(6, "HYPHelper.exe", 16 * 2**30, old, good_exe)
    r = kill_process.run("HYPHelper.exe", cfg, {}, psutil_mod=FakePS([p]))
    assert r["status"] == "degraded:access_denied", r

    # 7) 路径校验 fail-closed：exe() 返回 None → 不放行
    p = FakeProc(7, "HYPHelper.exe", 16 * 2**30, old, None)
    r = kill_process.run("HYPHelper.exe", cfg, {}, psutil_mod=FakePS([p]))
    assert r["status"] == "skipped:path_mismatch" and not p.killed, r

    # 8) create_time 竞态防护：kill 前 create_time 变化 → PID 已复用 → skip
    class ReusedProc(FakeProc):
        def create_time(self):
            return self.info["create_time"] + 999
    p = ReusedProc(8, "HYPHelper.exe", 16 * 2**30, old, good_exe)
    r = kill_process.run("HYPHelper.exe", cfg, {}, psutil_mod=FakePS([p]))
    assert r["status"] == "skipped:pid_reused" and not p.killed, r

    # 9) AUTOKILL 回落：WATCH 查不到则查 AUTOKILL，阈值=红线 20GB，全流程可杀
    #    熔断计数走 kv（与面板通道共用），杀成后 kv 里应记 1 次
    class C2:
        WATCH_PROCESSES = []
        AUTOKILL = ["BigApp.exe"]
    kv = {}
    p = FakeProc(9, "BigApp.exe", 21 * 2**30, old, r"D:\\anywhere\\BigApp.exe")
    r = kill_process.run("BigApp.exe", C2, {}, psutil_mod=FakePS([p]),
                         kv_get=lambda k: kv.get(k), kv_set=lambda k, v: kv.__setitem__(k, v))
    assert r["status"] == "killed" and p.killed, r
    import json as _json
    assert len(_json.loads(kv["kill_times"])["bigapp.exe"]) == 1, kv   # 分桶格式 {name: [ts...]}

    # 9b) AUTOKILL 阈值语义：19GB（<20GB 红线）不杀
    p = FakeProc(10, "BigApp.exe", 19 * 2**30, old, r"D:\\anywhere\\BigApp.exe")
    r = kill_process.run("BigApp.exe", C2, {}, psutil_mod=FakePS([p]))
    assert r["status"] == "skipped:commit_below_threshold" and not p.killed, r

    # 9c) AUTOKILL dict 形式带 path_contains：路径不匹配仍拒
    class C3:
        WATCH_PROCESSES = []
        AUTOKILL = [{"name": "BigApp.exe", "path_contains": "Games"}]
    p = FakeProc(11, "BigApp.exe", 21 * 2**30, old, r"C:\\evil\\BigApp.exe")
    r = kill_process.run("BigApp.exe", C3, {}, psutil_mod=FakePS([p]))
    assert r["status"] == "skipped:path_mismatch" and not p.killed, r

    # 10) NEVER_KILL 入口硬阻断：AUTOKILL 误配 explorer.exe 仍 blocked（防配置事故）
    class C4:
        WATCH_PROCESSES = []
        AUTOKILL = ["explorer.exe"]
    p = FakeProc(12, "explorer.exe", 25 * 2**30, old, r"C:\\Windows\\explorer.exe")
    r = kill_process.run("explorer.exe", C4, {}, psutil_mod=FakePS([p]))
    assert r["status"] == "blocked:never_kill" and not p.killed, r

    # 11) 白名单外进程仍拒杀（WATCH 与 AUTOKILL 都查不到）
    p = FakeProc(13, "notepad.exe", 30 * 2**30, old, None)
    r = kill_process.run("notepad.exe", C2, {}, psutil_mod=FakePS([p]))
    assert r["status"] == "skipped:not_whitelisted" and not p.killed, r

    # 12) memory_info=None 竞态（process_iter(attrs) 途中进程退出）：跳过不炸
    p_none = FakeProc(14, "HYPHelper.exe", 0, old, good_exe)
    p_none.info["memory_info"] = None
    p_ok = FakeProc(15, "HYPHelper.exe", 16 * 2**30, old, good_exe)
    r = kill_process.run("HYPHelper.exe", cfg, {}, psutil_mod=FakePS([p_none, p_ok]))
    assert r["status"] == "killed" and r["pid"] == 15 and not p_none.killed, r
    r = kill_process.run("HYPHelper.exe", cfg, {}, psutil_mod=FakePS([p_none]))
    assert r["status"] == "skipped:process_gone", r   # 全部 None → 视为无进程

    # 13) 熔断按进程名分桶：A 杀满 2 次被熔断，B 不受影响
    wA = {"name": "LeakA.exe", "max_commit_gb": 15, "min_age_s": 60, "max_kills_per_10min": 2}
    wB = dict(wA); wB["name"] = "LeakB.exe"
    class C5:
        WATCH_PROCESSES = [wA, wB]
    st5 = {}
    for pid in (20, 21):
        p = FakeProc(pid, "LeakA.exe", 16 * 2**30, old, None)
        r = kill_process.run("LeakA.exe", C5, st5, psutil_mod=FakePS([p]))
        assert r["status"] == "killed", r
    p = FakeProc(22, "LeakA.exe", 16 * 2**30, old, None)
    r = kill_process.run("LeakA.exe", C5, st5, psutil_mod=FakePS([p]))
    assert r["status"] == "degraded:frequency_cap" and not p.killed, r   # A 第 3 次熔断
    p = FakeProc(23, "LeakB.exe", 16 * 2**30, old, None)
    r = kill_process.run("LeakB.exe", C5, st5, psutil_mod=FakePS([p]))
    assert r["status"] == "killed" and p.killed, r   # B 额度独立
    assert set(st5["kill_times"]) == {"leaka.exe", "leakb.exe"}, st5

    # 14) kv 旧格式（批次 A 的纯列表）兼容：视为无 per-process 历史，可杀
    kv2 = {"kill_times": _json.dumps([now - 60, now - 30])}
    p = FakeProc(24, "LeakA.exe", 16 * 2**30, old, None)
    r = kill_process.run("LeakA.exe", C5, {}, psutil_mod=FakePS([p]),
                         kv_get=lambda k: kv2.get(k), kv_set=lambda k, v: kv2.__setitem__(k, v))
    assert r["status"] == "killed", r
    assert isinstance(_json.loads(kv2["kill_times"]), dict), kv2   # 写回即迁移为分桶格式

    # 14b) kv 分桶类型防护（终审修订）：桶非列表 / 含非数字元素 → 逐元素丢弃，不抛 TypeError
    kv3 = {"kill_times": _json.dumps({"leaka.exe": "notalist"})}
    p = FakeProc(25, "LeakA.exe", 16 * 2**30, old, None)
    r = kill_process.run("LeakA.exe", C5, {}, psutil_mod=FakePS([p]),
                         kv_get=lambda k: kv3.get(k), kv_set=lambda k, v: kv3.__setitem__(k, v))
    assert r["status"] == "killed", r   # 桶非列表视为无历史
    kv4 = {"kill_times": _json.dumps({"leaka.exe": ["x", None, now - 10]})}
    p = FakeProc(26, "LeakA.exe", 16 * 2**30, old, None)
    r = kill_process.run("LeakA.exe", C5, {}, psutil_mod=FakePS([p]),
                         kv_get=lambda k: kv4.get(k), kv_set=lambda k, v: kv4.__setitem__(k, v))
    assert r["status"] == "killed", r   # 非法元素丢弃，合法的 now-10 计入但不熔断
    times4 = _json.loads(kv4["kill_times"])["leaka.exe"]
    assert len(times4) == 2 and abs(times4[0] - (now - 10)) < 1, times4   # 非法元素已被滤除

    # 15) toast PowerShell 降级链转义：单引号双写（进程名/cmdline 可含 '，属注入面）
    script = toast._ps_script("烛龙's", "it's a 'leak'")
    assert "烛龙''s" in script and "it''s a ''leak''" in script, script
    assert "{TITLE}" not in script and "{BODY}" not in script, script
    print("PASS")


if __name__ == "__main__":
    main()

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

    # 5) 频次熔断：10 分钟内第 3 次 → degraded
    now = time.time()
    state = {"kill_times": [now - 60, now - 30]}
    p = FakeProc(5, "HYPHelper.exe", 16 * 2**30, old, good_exe)
    r = kill_process.run("HYPHelper.exe", cfg, state, psutil_mod=FakePS([p]))
    assert r["status"] == "degraded:frequency_cap" and not p.killed, r

    # 6) AccessDenied → degraded 且不抛异常
    class DenyProc(FakeProc):
        def kill(self): raise FakePS.AccessDenied("denied")
    p = DenyProc(6, "HYPHelper.exe", 16 * 2**30, old, good_exe)
    r = kill_process.run("HYPHelper.exe", cfg, {}, psutil_mod=FakePS([p]))
    assert r["status"] == "degraded:access_denied", r
    print("PASS")


if __name__ == "__main__":
    main()

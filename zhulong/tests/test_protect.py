"""关键进程识别（core/protect.py）：五信号 + fail-closed。"""
from zhulong.core import protect


class FakeProc:
    def __init__(self, pid=100, username=None, exe=None, exe_exc=None):
        self.pid = pid
        self._username, self._exe, self._exe_exc = username, exe, exe_exc

    def username(self):
        if self._username is None:
            raise RuntimeError("denied")
        return self._username

    def exe(self):
        if self._exe_exc is not None:
            raise self._exe_exc
        return self._exe


def main():
    # 信号 1：安全软件名单（红线"永不处置"代码化）
    for name in ("qqpctray.exe", "msmpeng.exe", "hipsdaemon.exe", "360tray.exe"):
        hit, reason = protect.is_critical(name)
        assert hit and reason == "safety_software", (name, reason)

    # 信号 2：保护名单（NEVER_KILL 与 v1.6 补充集）
    for name in ("explorer.exe", "lsass.exe", "smss.exe", "wininit.exe", "fontdrvhost.exe"):
        hit, reason = protect.is_critical(name)
        assert hit and reason == "never_kill", (name, reason)

    # 干净名字 + 无 proc 对象：不关键（名单级信息不足时不误伤）
    hit, reason = protect.is_critical("someapp.exe")
    assert not hit, (hit, reason)

    # 信号 3：SYSTEM 账户 → 系统进程；普通用户不触发
    hit, reason = protect.is_critical("someapp.exe",
                                      FakeProc(username=r"NT AUTHORITY\SYSTEM", exe=r"C:\x\a.exe"))
    assert hit and reason == "system_account", reason
    hit, _ = protect.is_critical("someapp.exe",
                                 FakeProc(username=r"NT AUTHORITY\LOCAL SERVICE", exe=r"C:\x\a.exe"))
    assert hit, "LOCAL SERVICE 也应判系统账户"
    hit, _ = protect.is_critical("someapp.exe",
                                 FakeProc(username=r"DESKTOP\user", exe=r"C:\x\a.exe"))
    assert not hit, "普通用户账户误判关键"

    # 信号 4：System32 / SysWOW64 路径（大小写不敏感）；普通路径不触发
    for p in (r"C:\WINDOWS\system32\a.exe", r"C:\Windows\SysWOW64\a.exe"):
        hit, reason = protect.is_critical("someapp.exe", FakeProc(username=r"DESKTOP\u", exe=p))
        assert hit and reason == "system_path", (p, reason)
    hit, _ = protect.is_critical("someapp.exe",
                                 FakeProc(username=r"DESKTOP\u", exe=r"C:\Program Files\app\a.exe"))
    assert not hit, "普通路径误判关键"

    # fail-closed：exe 读不到 / 为空 → 判关键（username 读不到不误判，交给路径信号兜底）
    hit, reason = protect.is_critical("someapp.exe", FakeProc(exe_exc=RuntimeError("denied")))
    assert hit and reason == "exe_unreadable", reason
    hit, reason = protect.is_critical("someapp.exe", FakeProc(exe=""))
    assert hit and reason == "exe_unreadable", reason
    hit, _ = protect.is_critical("someapp.exe",
                                 FakeProc(username=None, exe=r"C:\app\a.exe"))
    assert not hit, "username 读不到但路径干净：不应误判"

    # 信号 5：前台进程（monkeypatch foreground_pid；真机路径由 memtrim 巡检覆盖）
    orig = protect.foreground_pid
    protect.foreground_pid = lambda: 555
    try:
        hit, reason = protect.is_critical("someapp.exe",
                                          FakeProc(pid=555, username=r"DESKTOP\u", exe=r"C:\app\a.exe"))
        assert hit and reason == "foreground", reason
        hit, _ = protect.is_critical("someapp.exe",
                                     FakeProc(pid=556, username=r"DESKTOP\u", exe=r"C:\app\a.exe"))
        assert not hit, "非前台同路径进程误判关键"
        # foreground_pid 抛异常：不误伤（返回 None 语义）
        protect.foreground_pid = lambda: (_ for _ in ()).throw(RuntimeError("no hwnd"))
        hit, _ = protect.is_critical("someapp.exe",
                                     FakeProc(pid=555, username=r"DESKTOP\u", exe=r"C:\app\a.exe"))
        assert not hit, "前台信号异常导致误判"
    finally:
        protect.foreground_pid = orig

    # name_blocked：无 proc 的名单级判定（供安全链在枚举前调用）
    assert protect.name_blocked("qqpctray.exe") == (True, "safety_software")
    assert protect.name_blocked("wininit.exe") == (True, "never_kill")
    assert protect.name_blocked("someapp.exe") == (False, "")
    print("PASS")


if __name__ == "__main__":
    main()

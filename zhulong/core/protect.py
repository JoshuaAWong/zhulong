"""关键进程识别：供温和处置动作（mem_trim / restart_service）在动手前调用。
多重信号 fail-closed——识别不了的一律当关键进程对待，宁可少清一个进程不可误伤关键进程。
kill 链不用本模块（继续用 anomaly.NEVER_KILL 入口硬阻断，职责分离）。
识别边界：不做数字签名校验（fail-closed 下误放行为零、误拦截只是少清一个进程，代价不对称可接受）。"""
import ctypes
import ctypes.wintypes as wintypes

from zhulong.core.anomaly import NEVER_KILL

# 安全软件（红线"永不处置安全软件"的代码化）：腾讯电脑管家 / 360 / 火绒 / Defender
SAFE_SOFTWARE = {
    "qqpctray.exe", "qqpcrtp.exe", "qqpcmgr.exe",                       # 腾讯电脑管家
    "360tray.exe", "360safe.exe", "zhudongfangyu.exe", "360sd.exe",     # 360 安全卫士
    "hipsdaemon.exe", "usysdiag.exe", "wsctrl.exe",                     # 火绒安全
    "msmpeng.exe", "msascuil.exe", "nissrv.exe",                        # Windows Defender
}

# NEVER_KILL 之外的补充关键进程（动了会蓝屏/会话崩溃）
NEVER_TOUCH_EXTRA = {"smss.exe", "wininit.exe", "fontdrvhost.exe"}

_user32 = ctypes.windll.user32


def foreground_pid():
    """当前前台窗口所属进程 PID（trim 前台进程会立刻引发换页卡顿）。读不到返回 None。"""
    hwnd = _user32.GetForegroundWindow()
    if not hwnd:
        return None
    pid = wintypes.DWORD()
    if not _user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid)):
        return None
    return pid.value


def name_blocked(name_lower):
    """名单级阻断（无需 Process 对象，先于一切枚举调用）。返回 (bool, reason)。"""
    if name_lower in SAFE_SOFTWARE:
        return True, "safety_software"
    if name_lower in NEVER_KILL or name_lower in NEVER_TOUCH_EXTRA:
        return True, "never_kill"
    return False, ""


def is_critical(name_lower, proc=None):
    """判定进程是否关键（不可温和处置）。返回 (True, reason) 或 (False, "")。
    proc 为 psutil.Process 时启用账户/路径/前台信号；exe 读不到一律判关键（fail-closed，
    镜像 kill 链路径校验语义）。"""
    hit, reason = name_blocked(name_lower)
    if hit:
        return True, reason
    if proc is None:
        return False, ""
    try:
        user = (proc.username() or "").lower()
    except Exception:
        user = ""   # 账户读不到：交给后面 fail-closed 的路径信号兜底
    if "nt authority" in user:
        return True, "system_account"
    try:
        exe = (proc.exe() or "").lower()
    except Exception:
        return True, "exe_unreadable"
    if not exe:
        return True, "exe_unreadable"
    if "\\windows\\system32\\" in exe or "\\windows\\syswow64\\" in exe:
        return True, "system_path"
    try:
        fg = foreground_pid()
    except Exception:
        fg = None
    if fg is not None and getattr(proc, "pid", None) == fg:
        return True, "foreground"
    return False, ""

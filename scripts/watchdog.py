"""烛龙看门狗：计划任务每 5 分钟调用。
判定纯函数 decide 可单测；restart = 杀旧 PID（若在）→ 分离模式拉起新实例。"""
import ctypes
import csv
import io
import json
import os
import subprocess
import sys
import time
from pathlib import Path

PROJECT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(PROJECT))
from zhulong import config   # noqa: E402

CREATE_NO_WINDOW = 0x08000000
DETACHED_PROCESS = 0x00000008


def decide(heartbeat, now, intent_exists, stale_s):
    if intent_exists:
        return "noop"
    if heartbeat is None:
        return "restart"
    if now - float(heartbeat.get("ts", 0)) > stale_s:
        return "restart"
    return "noop"


def _pid_alive(pid, out=None):
    """PID 存活且映像名为 pythonw.exe 才算烛龙活着——
    烛龙崩溃后 PID 可能被无关进程复用，只看 PID 存在会误杀无辜进程。"""
    # System ProcessIdle(0) 与不存在一律视为不可杀
    if not pid or int(pid) <= 0:
        return False
    if out is None:
        # tasklist 输出为系统 ANSI 码页（中文 Windows 为 GBK），需容错解码
        out = subprocess.run(["tasklist", "/FI", f"PID eq {pid}", "/FO", "CSV", "/NH"],
                             capture_output=True, creationflags=CREATE_NO_WINDOW,
                             text=True, errors="replace").stdout
    for row in csv.reader(io.StringIO(out)):
        # CSV 列：映像名,PID,会话名,会话号,内存使用；过滤无命中时输出的是提示行（非 CSV）
        if len(row) >= 2 and row[1].strip() == str(pid):
            return row[0].strip().lower() == "pythonw.exe"
    return False


def main():
    hb_path = config.STATE_DIR / "heartbeat.json"
    intent = config.STATE_DIR / "stopped.intent"
    hb = None
    if hb_path.exists():
        try:
            hb = json.loads(hb_path.read_text(encoding="utf-8"))
        except Exception:
            hb = None
    action = decide(hb, time.time(), intent.exists(), config.HEARTBEAT_STALE_S)
    if action != "restart":
        return
    if hb and hb.get("pid") != os.getpid() and _pid_alive(hb.get("pid")):
        subprocess.run(["taskkill", "/F", "/PID", str(hb["pid"])], capture_output=True,
                     creationflags=CREATE_NO_WINDOW)
        time.sleep(2)
    subprocess.Popen([str(config.PYTHONW), "-X", "utf8", "-m", "zhulong"],
                     cwd=str(PROJECT), creationflags=DETACHED_PROCESS | CREATE_NO_WINDOW)
    print(f"[watchdog] {time.strftime('%F %T')} 已拉起烛龙")


if __name__ == "__main__":
    main()

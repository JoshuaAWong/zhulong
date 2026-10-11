"""AC-7 采样（pythonw 无窗口版，替代 powershell 弹窗方案）：
每小时按心跳文件 PID 精确定位烛龙本体，记录其内存/CPU 到 data/logs/ac7.log。
不按 pythonw.exe 名字汇总全机——机器上可能有别的 pythonw 进程。"""
import json
import sys
import time
from pathlib import Path

PROJECT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(PROJECT))
from zhulong import config   # noqa: E402

import psutil   # noqa: E402

LOG = config.DATA_DIR / "logs" / "ac7.log"


def zhulong_pid():
    """心跳文件里的 PID 即烛龙本体；读不到/非法返回 None。"""
    try:
        pid = int(json.loads((config.STATE_DIR / "heartbeat.json")
                             .read_text(encoding="utf-8"))["pid"])
        return pid if pid > 0 else None
    except Exception:
        return None


def main():
    pid = zhulong_pid()
    mem_mb, cpu_s, note = 0, 0, ""
    if pid is None:
        note = " no-heartbeat"
    else:
        try:
            p = psutil.Process(pid)
            # 防 PID 复用：映像名须为 pythonw（与看门狗同口径）
            if p.name().lower() == "pythonw.exe":
                mem_mb = round(p.memory_info().rss / 1024 / 1024)
                ct = p.cpu_times()
                cpu_s = round(ct.user + ct.system)
            else:
                note = f" pid={pid} reused-by-{p.name()}"
        except (psutil.NoSuchProcess, psutil.AccessDenied):
            note = f" pid={pid} gone-or-denied"
    LOG.parent.mkdir(parents=True, exist_ok=True)
    with LOG.open("a", encoding="utf-8") as f:
        f.write(f"{time.strftime('%m-%d %H:%M')} mem={mem_mb}MB cpu={cpu_s}s{note}\n")


if __name__ == "__main__":
    main()

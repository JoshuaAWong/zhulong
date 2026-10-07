"""AC-7 采样（pythonw 无窗口版，替代 powershell 弹窗方案）：
每小时记录烛龙自身（pythonw）内存/CPU 到 data/logs/ac7.log。"""
import time
from pathlib import Path

import psutil

LOG = Path(__file__).resolve().parent.parent / "data" / "logs" / "ac7.log"


def main():
    procs = [p for p in psutil.process_iter(["name", "memory_info"])
             if (p.info["name"] or "").lower() == "pythonw.exe"]
    mem_mb = round(sum(p.info["memory_info"].rss for p in procs) / 1024 / 1024)
    cpu_s = round(sum(p.cpu_times().user + p.cpu_times().system for p in procs))
    LOG.parent.mkdir(parents=True, exist_ok=True)
    with LOG.open("a", encoding="utf-8") as f:
        f.write(f"{time.strftime('%m-%d %H:%M')} mem={mem_mb}MB cpu={cpu_s}s n={len(procs)}\n")


if __name__ == "__main__":
    main()

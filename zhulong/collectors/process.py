"""白名单进程 commit 采集：只看 config.WATCH_PROCESSES 中列出的进程。"""
import psutil

from zhulong import config as default_config

GB = 1024 ** 3


def collect(cfg, process_iter=None):
    cfg = cfg or default_config
    targets = {w["name"].lower(): w for w in cfg.WATCH_PROCESSES}
    rows = []
    found = {}
    if process_iter is not None:
        procs = process_iter()
    else:
        procs = psutil.process_iter(["pid", "name", "memory_info"])
    for p in procs:
        name = (p.info["name"] or "").lower()
        if name in targets:
            if "memory_commit" in p.info:
                commit = p.info["memory_commit"]
            else:
                # Windows 语义：psutil 无 per-process commit 专用字段时，memory_info().vms
                # 即 PagefileUsage（进程 commit charge），作为回退
                mi = p.info["memory_info"]
                commit = getattr(mi, "commit", None)
                if not commit:
                    commit = mi.vms
            found[name] = (p.info["pid"], commit / GB)
    for name in targets:
        # key 保留白名单原始大小写（如 HYPHelper_commit_gb），匹配仅用小写做键
        key = f"{targets[name]['name'].rsplit('.', 1)[0]}_commit_gb"
        if name in found:
            pid, gb = found[name]
            rows.append((key, round(gb, 2), str(pid)))
        else:
            rows.append((key, 0.0, "absent"))
    return rows


COLLECTOR = {"name": "process", "interval": 1, "collect": collect}

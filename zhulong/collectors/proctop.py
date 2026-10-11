"""统一进程大户采集：分频由调度器统一实现（COLLECTOR["interval"]=4 = 2 分钟一轮），
一趟同时产出 CPU 大户、IO 大户、commit Top5——扫描次数最少化（v1.2 里 cpu/diskio
各自的进程扫描已下线合并于此）。进程名放 label。"""
import psutil

GB = 1024 ** 3
_PROC_CACHE = {}
_CMD_MAX = 200   # label 里 cmdline 截断上限：实测长 label 占 DB 日写入字节 87%
# 系统伪进程：不计入大户（System Idle Process 的 cpu_percent 是"空闲率"而非消耗）
_SKIP_NAMES = {"system idle process", "system", "registry", "memory compression"}


def _scan(cfg, process_iter=None):
    targets = {"cpu": ("", 0.0), "io": ("", 0.0), "commit": []}
    alive = set()
    for p in (process_iter or psutil.process_iter)(["pid", "name", "memory_info"]):
        try:
            pid = p.info["pid"]
            alive.add(pid)
            name = p.info["name"] or ""
            if not name or pid == 0 or name.lower() in _SKIP_NAMES:
                continue
            proc = _PROC_CACHE.setdefault(pid, p)
            cpu = proc.cpu_percent()
            if cpu > targets["cpu"][1]:
                targets["cpu"] = (name, cpu)
            try:
                io = proc.io_counters()
                b = (io.read_bytes + io.write_bytes) / 1024 / 1024
                if b > targets["io"][1]:
                    targets["io"] = (name, b)
            except (psutil.NoSuchProcess, psutil.AccessDenied):
                pass
            mi = p.info["memory_info"]
            commit = getattr(mi, "commit", None) or mi.vms
            targets["commit"].append((commit / GB, name, pid))
        except (psutil.NoSuchProcess, psutil.AccessDenied):
            continue
    for pid in list(_PROC_CACHE):
        if pid not in alive:
            _PROC_CACHE.pop(pid, None)
    targets["commit"].sort(reverse=True)
    return targets


def collect(cfg, scan_fn=None):
    t = (scan_fn or _scan)(cfg)
    rows = []
    if t["cpu"][0]:
        rows.append(("cpu_top_pct", round(t["cpu"][1], 1), t["cpu"][0]))
    if t["io"][0]:
        rows.append(("io_top_mb", round(t["io"][1], 1), t["io"][0]))
    for i, entry in enumerate(t["commit"][:5], 1):
        gb, name = entry[0], entry[1]
        pid = entry[2] if len(entry) > 2 else None
        # 大户附 PID 与命令行：同路径多实例（双开 java/浏览器）靠 pid 区分，
        # JVM/解释器类的真实身份看启动参数（-jar / 脚本路径）
        cmd = ""
        if pid is not None:
            try:
                cmd = " ".join(_PROC_CACHE[pid].cmdline())
            except Exception:
                pass
        label = name
        if pid:
            label += f"|{pid}"
        if cmd:
            if len(cmd) > _CMD_MAX:
                cmd = cmd[:_CMD_MAX] + "…"   # 超长命令行截断，控制 DB 写入量
            label += f"|{cmd}"
        rows.append((f"proctop{i}", round(gb, 2), label))
    return rows


COLLECTOR = {"name": "proctop", "interval": 4, "collect": collect}   # 30s×4 = 2 分钟一轮

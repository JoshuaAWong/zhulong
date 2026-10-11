"""统一进程大户采集：分频由调度器统一实现（COLLECTOR["interval"]=4 = 2 分钟一轮），
一趟同时产出 CPU 大户、IO 大户、commit Top5——扫描次数最少化（v1.2 里 cpu/diskio
各自的进程扫描已下线合并于此）。进程名放 label。
IO 大户（B4）：取差分速率 MB/s（累计计数会岁长跑进程永久霸榜）；
_PID 复用防护：缓存条目带 create_time 校验，新进程命中旧 pid 缓存时丢弃旧计数
（psutil 的 cpu_percent 内部差分与本模块 IO 差分都依赖此缓存）。"""
import time

import psutil

GB = 1024 ** 3
_PROC_CACHE = {}   # pid -> proc（cpu_percent 内部差分依赖同一 proc 对象连续调用）
_IO_PREV = {}      # pid -> (累计 MB, ts)：IO 大户差分速率的上次计数
_CMD_MAX = 200   # label 里 cmdline 截断上限：实测长 label 占 DB 日写入字节 87%
# 系统伪进程：不计入大户（System Idle Process 的 cpu_percent 是"空闲率"而非消耗）
_SKIP_NAMES = {"system idle process", "system", "registry", "memory compression"}


def _scan(cfg, process_iter=None, now=None):
    targets = {"cpu": ("", 0.0), "io": ("", 0.0), "commit": []}
    alive = set()
    now = time.time() if now is None else now
    for p in (process_iter or psutil.process_iter)(["pid", "name", "memory_info", "create_time"]):
        try:
            pid = p.info["pid"]
            alive.add(pid)
            name = p.info["name"] or ""
            if not name or pid == 0 or name.lower() in _SKIP_NAMES:
                continue
            proc = _PROC_CACHE.get(pid)
            if proc is not None and proc.info.get("create_time") != p.info.get("create_time"):
                proc = None                 # PID 复用：旧缓存计数属于已死进程，丢弃重来
                _IO_PREV.pop(pid, None)
            if proc is None:
                proc = p
                _PROC_CACHE[pid] = p
            cpu = proc.cpu_percent()
            if cpu > targets["cpu"][1]:
                targets["cpu"] = (name, cpu)
            try:
                io = proc.io_counters()
                b = (io.read_bytes + io.write_bytes) / 1024 / 1024
                prev = _IO_PREV.get(pid)
                _IO_PREV[pid] = (b, now)
                if prev is not None:   # 首采只建基线不出数（差分需要两点）
                    rate = max(b - prev[0], 0.0) / max(now - prev[1], 1e-6)   # MB/s
                    if rate > targets["io"][1]:
                        targets["io"] = (name, rate)
            except (psutil.NoSuchProcess, psutil.AccessDenied):
                pass
            mi = p.info.get("memory_info")
            if mi is None:
                continue   # 竞态：遍历途中进程退出
            commit = getattr(mi, "commit", None) or mi.vms
            targets["commit"].append((commit / GB, name, pid))
        except (psutil.NoSuchProcess, psutil.AccessDenied):
            continue
    for pid in list(_PROC_CACHE):
        if pid not in alive:
            _PROC_CACHE.pop(pid, None)
            _IO_PREV.pop(pid, None)
    targets["commit"].sort(reverse=True)
    return targets


def collect(cfg, scan_fn=None):
    t = (scan_fn or _scan)(cfg)
    rows = []
    if t["cpu"][0]:
        rows.append(("cpu_top_pct", round(t["cpu"][1], 1), t["cpu"][0]))
    if t["io"][0]:
        rows.append(("io_top_mb_s", round(t["io"][1], 1), t["io"][0]))
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

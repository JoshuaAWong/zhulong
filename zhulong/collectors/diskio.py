"""磁盘 IO 采集：系统读写速率、最忙的盘、可移动磁盘活动、读写最猛的进程。
速率由计数器差分/采样间隔得到；进程级扫描与 cpu 采集器同频分频。"""
import time

import psutil

_PREV = {"t": None, "counters": None}


def _disk_rates():
    """返回 (read_mb_s, write_mb_s, busiest_disk_label)。"""
    now = time.time()
    cur = psutil.disk_io_counters(perdisk=True)
    total = psutil.disk_io_counters()
    prev, prev_t = _PREV["counters"], _PREV["t"]
    _PREV["counters"], _PREV["t"] = (cur, total), now
    if prev is None:
        return 0.0, 0.0, ""
    dt = max(now - prev_t, 1e-6)
    read = (total.read_bytes - prev[1].read_bytes) / dt / 1024 / 1024
    write = (total.write_bytes - prev[1].write_bytes) / dt / 1024 / 1024
    busiest, busiest_v = "", -1.0
    for disk, c in cur.items():
        dv = ((c.read_bytes + c.write_bytes) - (prev[0][disk].read_bytes + prev[0][disk].write_bytes)) / dt if disk in prev[0] else 0
        if dv > busiest_v:
            busiest, busiest_v = disk.rstrip("\\:"), dv
    return round(max(read, 0), 2), round(max(write, 0), 2), busiest


def _removable_active():
    """可移动磁盘（U盘/移动硬盘）是否存在。"""
    try:
        for p in psutil.disk_partitions():
            if "removable" in (p.opts or "").lower():
                return p.device.rstrip("\\:")
    except Exception:
        pass
    return ""


def _top_io_process(cfg):
    best_name, best_bps = "", 0.0
    for p in psutil.process_iter(["pid", "name"]):
        try:
            io = p.io_counters()
            bps = io.read_bytes + io.write_bytes
            if bps > best_bps and (p.info["name"] or ""):
                best_name, best_bps = p.info["name"], bps
        except (psutil.NoSuchProcess, psutil.AccessDenied):
            continue
    return best_name, round(best_bps / 1024 / 1024, 1)   # 累计 MB（总量非速率，作量级参考）


def collect(cfg, rates_fn=None, removable_fn=None, top_fn=None):
    read, write, busiest = (rates_fn or _disk_rates)()
    removable = (removable_fn or _removable_active)()
    rows = [
        ("io_read_mb_s", read, busiest),
        ("io_write_mb_s", write, busiest),
    ]
    if removable:
        rows.append(("io_remount", 1.0, removable))   # 1=存在可移动磁盘
    state = collect.__dict__.setdefault("_state", {"cycle": 0})
    state["cycle"] += 1
    if state["cycle"] % getattr(cfg, "CPU_TOP_INTERVAL", 4) == 0:
        name, mb = (top_fn or _top_io_process)(cfg)
        rows.append(("io_top_mb", mb, name))
    return rows


COLLECTOR = {"name": "diskio", "interval": 1, "collect": collect}

"""CPU 采集：总占用、单核峰值、吃 CPU 最多的进程。
总量指标每周期都跑；进程级扫描走 interval 分频（config.CPU_TOP_INTERVAL 个周期一次，默认 4 = 2 分钟）。"""
import psutil

_PROC_CACHE = {}   # pid -> Process，复用对象才能累计 cpu_percent 差分


def _top_process(cfg):
    """返回 (name, percent)；进程级 cpu_percent 依赖上周期基线，首轮近似为 0。"""
    best_name, best_pct = "", 0.0
    for p in psutil.process_iter(["pid", "name"]):
        try:
            proc = _PROC_CACHE.setdefault(p.info["pid"], p)
            pct = proc.cpu_percent()
            if pct > best_pct and (p.info["name"] or ""):
                best_name, best_pct = p.info["name"], pct
        except (psutil.NoSuchProcess, psutil.AccessDenied):
            continue
    # 清理已退出进程的缓存
    alive = {p.info["pid"] for p in psutil.process_iter(["pid"])}
    for pid in list(_PROC_CACHE):
        if pid not in alive:
            _PROC_CACHE.pop(pid, None)
    return best_name, round(best_pct, 1)


def collect(cfg, cpu_percent=None, percpu=None, top_fn=None):
    total = cpu_percent if cpu_percent is not None else psutil.cpu_percent()
    cores = percpu if percpu is not None else psutil.cpu_percent(percpu=True)
    rows = [
        ("cpu_percent", round(float(total), 1), ""),
        ("cpu_max_core", round(max(cores) if cores else 0.0, 1), ""),
    ]
    state = collect.__dict__.setdefault("_state", {"cycle": 0})
    state["cycle"] += 1
    if state["cycle"] % getattr(cfg, "CPU_TOP_INTERVAL", 4) == 0:
        name, pct = (top_fn or _top_process)(cfg)
        rows.append(("cpu_top_pct", pct, name))   # 大户名放 label
    return rows


COLLECTOR = {"name": "cpu", "interval": 1, "collect": collect}

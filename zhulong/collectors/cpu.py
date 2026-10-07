"""CPU 采集：总占用与单核峰值（零成本计数器读取）。
进程级"CPU 大户"已统一并入 proctop.py（一次遍历产三类大户）。"""
import psutil


def collect(cfg, cpu_percent=None, percpu=None):
    total = cpu_percent if cpu_percent is not None else psutil.cpu_percent()
    cores = percpu if percpu is not None else psutil.cpu_percent(percpu=True)
    return [
        ("cpu_percent", round(float(total), 1), ""),
        ("cpu_max_core", round(max(cores) if cores else 0.0, 1), ""),
    ]


COLLECTOR = {"name": "cpu", "interval": 1, "collect": collect}

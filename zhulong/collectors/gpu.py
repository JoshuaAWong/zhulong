"""GPU 采集（NVIDIA，nvidia-smi）：温度/利用率/显存，一次查询取全。
分频每 GPU_INTERVAL 个周期一次（默认 2 = 1 分钟），抑制外部进程开销。
CPU 温度 WMI 实测不可得，不采集（不引入常驻第三方服务）。"""
import subprocess

CREATE_NO_WINDOW = 0x08000000


def _query():
    try:
        out = subprocess.run(
            ["nvidia-smi", "--query-gpu=temperature.gpu,utilization.gpu,memory.used,memory.total",
             "--format=csv,noheader,nounits"],
            capture_output=True, text=True, timeout=5, creationflags=CREATE_NO_WINDOW)
        if out.returncode != 0:
            return None
        temp, util, mu, mt = [x.strip() for x in out.stdout.strip().split(",")]
        return float(temp), float(util), float(mu) / 1024, float(mt) / 1024
    except Exception:
        return None


def collect(cfg, query_fn=None):
    state = collect.__dict__.setdefault("_state", {"cycle": 0})
    state["cycle"] += 1
    if state["cycle"] % getattr(cfg, "GPU_INTERVAL", 2) != 0:
        return []
    r = (query_fn or _query)()
    if r is None:
        return []
    temp, util, mu, mt = r
    return [("gpu_temp", round(temp, 1), ""), ("gpu_util", round(util, 1), ""),
            ("gpu_mem_used", round(mu, 1), ""), ("gpu_mem_total", round(mt, 1), "")]


COLLECTOR = {"name": "gpu", "interval": 1, "collect": collect}

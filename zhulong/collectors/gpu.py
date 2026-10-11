"""GPU 采集（NVIDIA，nvidia-smi）：温度/利用率/显存，一次查询取全。
分频由调度器统一实现（COLLECTOR["interval"]=2 = 1 分钟一轮），抑制外部进程开销。
CPU 温度 WMI 实测不可得，不采集（不引入常驻第三方服务）。"""
import subprocess
import time

CREATE_NO_WINDOW = 0x08000000
_NO_GPU = False   # 负缓存：首次 FileNotFoundError（无 N 卡）后永久跳过，不再每分钟重试
_LAST = {}        # on-change：gpu_mem_total 值不变不重复落库（每日至少一行兜底）


def _parse(out_text):
    """解析 nvidia-smi csv 输出：多卡取首行；逐字段容错 'N/A'/空/非法值 → None
    （该字段跳过不产出，不炸整组）。返回 (temp, util, mem_used_gb, mem_total_gb)。"""
    lines = out_text.strip().splitlines()
    if not lines:
        return None
    vals = [x.strip() for x in lines[0].split(",")]
    if len(vals) < 4:
        return None

    def f(i, scale=1.0):
        try:
            return float(vals[i]) / scale
        except (ValueError, IndexError):
            return None   # 'N/A' 等

    return f(0), f(1), f(2, 1024.0), f(3, 1024.0)


def _query():
    global _NO_GPU
    try:
        out = subprocess.run(
            ["nvidia-smi", "--query-gpu=temperature.gpu,utilization.gpu,memory.used,memory.total",
             "--format=csv,noheader,nounits"],
            capture_output=True, text=True, timeout=5, creationflags=CREATE_NO_WINDOW)
        if out.returncode != 0:
            return None
        return _parse(out.stdout)
    except FileNotFoundError:
        _NO_GPU = True   # 无 N 卡：永久跳过
        return None
    except Exception:
        return None


def collect(cfg, query_fn=None):
    if _NO_GPU:
        return []
    r = (query_fn or _query)()
    if r is None:
        return []
    temp, util, mu, mt = r
    rows = []
    for key, v in (("gpu_temp", temp), ("gpu_util", util), ("gpu_mem_used", mu)):
        if v is not None:
            rows.append((key, round(v, 1), ""))
    if mt is not None:
        mt = round(mt, 1)
        today = time.strftime("%Y-%m-%d")
        if _LAST.get("gpu_mem_total") != (today, mt):
            _LAST["gpu_mem_total"] = (today, mt)
            rows.append(("gpu_mem_total", mt, ""))
    return rows


COLLECTOR = {"name": "gpu", "interval": 2, "collect": collect}   # 30s×2 = 1 分钟一轮

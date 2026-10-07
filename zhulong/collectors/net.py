"""网络采集：上下行速率（计数器差分，零成本）+ 延迟/抖动（ping）。
明示：本采集器是烛龙首个主动发包功能——每周期对网关与公网 DNS 各发 1 个 ICMP 包，
目标与开关走 config（PING_HOST / PING_ENABLED）。"""
import re
import statistics
import subprocess
import time

import psutil

_PREV = {"t": None, "c": None}
_PINGS = {"gw": [], "net": []}
CREATE_NO_WINDOW = 0x08000000


def _rates():
    now = time.time()
    cur = psutil.net_io_counters()
    prev, pt = _PREV["c"], _PREV["t"]
    _PREV["c"], _PREV["t"] = cur, now
    if prev is None:
        return 0.0, 0.0
    dt = max(now - pt, 1e-6)
    down = (cur.bytes_recv - prev.bytes_recv) / dt / 1024 / 1024
    up = (cur.bytes_sent - prev.bytes_sent) / dt / 1024 / 1024
    return round(max(down, 0.0), 2), round(max(up, 0.0), 2)


def _gateway():
    """默认网关：解析 ipconfig 输出（中文 Windows 为 GBK）。"""
    try:
        out = subprocess.run(["ipconfig"], capture_output=True, timeout=5,
                             creationflags=CREATE_NO_WINDOW).stdout.decode("gbk", errors="replace")
        m = re.search(r"(?:默认网关|Default Gateway)[.\s:：]*?(\d+\.\d+\.\d+\.\d+)", out)
        return m.group(1) if m else None
    except Exception:
        return None


def _ping(host, timeout_ms=800):
    """返回延迟 ms；失败返回 None。Windows ping 输出为 GBK。"""
    try:
        out = subprocess.run(["ping", "-n", "1", "-w", str(timeout_ms), host],
                             capture_output=True, timeout=timeout_ms / 1000 + 2,
                             creationflags=CREATE_NO_WINDOW).stdout.decode("gbk", errors="replace")
        m = re.search(r"(?:时间|time)[=<](\d+)\s*ms", out, re.I) or re.search(r"平均\s*=\s*(\d+)\s*ms", out)
        return float(m.group(1)) if m else None
    except Exception:
        return None


def collect(cfg, rates_fn=None, ping_fn=None, gw_fn=None):
    rows = []
    down, up = (rates_fn or _rates)()
    rows += [("net_down_mb_s", down, ""), ("net_up_mb_s", up, "")]

    if getattr(cfg, "PING_ENABLED", True):
        pf = ping_fn or _ping
        gw = (gw_fn or _gateway)()
        g = pf(gw) if gw else None
        n = pf(getattr(cfg, "PING_HOST", "223.5.5.5"))
        for key, val in (("gw", g), ("net", n)):
            if val is not None:
                _PINGS[key].append(val)
                _PINGS[key][:] = _PINGS[key][-10:]
        if g is not None:
            rows.append(("ping_gw_ms", g, ""))
        if n is not None:
            rows.append(("ping_net_ms", n, ""))
        jit = statistics.pstdev(_PINGS["net"]) if len(_PINGS["net"]) >= 3 else 0.0
        rows.append(("ping_jitter_ms", round(jit, 1), ""))
    return rows


COLLECTOR = {"name": "net", "interval": 1, "collect": collect}

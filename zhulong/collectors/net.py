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


def _parse_default_gateway(out):
    """解析 route print -4 输出：找目的 0.0.0.0 / 掩码 0.0.0.0 的默认路由行，
    多条时按跃点数（metric）取最小者的网关列。纯函数可测。
    IPv4 路由表行的列为：网络目标、网络掩码、网关、接口、跃点数。"""
    best, best_metric = None, None
    for line in out.splitlines():
        cols = line.split()
        if len(cols) < 5 or cols[0] != "0.0.0.0" or cols[1] != "0.0.0.0":
            continue
        gw = cols[2]
        if not re.match(r"^\d+\.\d+\.\d+\.\d+$", gw):
            continue   # "在链路上"等非 IP 网关不可 ping
        try:
            metric = int(cols[4])
        except ValueError:
            continue
        if best is None or metric < best_metric:
            best, best_metric = gw, metric
    return best


def _gateway():
    """默认网关：解析 route print -4（中文 Windows 为 GBK）。
    旧实现用 ipconfig 正则抓「默认网关」，多网卡/VPN 场景常抓错或抓不到。"""
    try:
        out = subprocess.run(["route", "print", "-4"], capture_output=True, timeout=5,
                             creationflags=CREATE_NO_WINDOW).stdout.decode("gbk", errors="replace")
        return _parse_default_gateway(out)
    except Exception:
        return None


_GW_CACHE = {"ip": None}   # 模块级缓存：命中直接用，仅 ping 失败才重新解析


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
        if gw_fn is not None:
            gw = gw_fn()
            g = pf(gw) if gw else None
        else:
            # 缓存命中直接用；仅 ping 网关失败时才重新解析 route print（换网/VPN 切换场景）
            gw = _GW_CACHE["ip"]
            if gw is not None:
                g = pf(gw)
                if g is None:
                    gw = _gateway()
                    g = pf(gw) if gw else None
            else:
                gw = _gateway()
                g = pf(gw) if gw else None
            _GW_CACHE["ip"] = gw
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

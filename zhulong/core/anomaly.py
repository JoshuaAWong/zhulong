"""通用异常进程判定：从 proctop 序列识别"疑似泄漏"——看增长不看大小。
纯函数可测：输入序列字典 {name|pid: [(ts, gb), ...]}（纯 name 亦可），输出异常列表（归并为纯 name）。
判定（满足其一）：
  ① 窗口内增长 > GROWTH_GB 且 > GROWTH_RATIO（且当前 > FLOOR_GB）→ suspect（疑似泄漏）
  ② 当前 > REDLINE_GB（且不在保护名单）→ redline（红线异常）
保护名单 NEVER_KILL 内进程只记录不判定。"""
ANOMALY_FLOOR_GB = 8.0
ANOMALY_GROWTH_GB = 4.0
ANOMALY_GROWTH_RATIO = 0.5
ANOMALY_REDLINE_GB = 20.0
ANOMALY_WINDOW_S = 30 * 60

NEVER_KILL = {
    "system", "registry", "memory compression", "system idle process",
    "explorer.exe", "dwm.exe", "csrss.exe", "winlogon.exe", "lsass.exe", "services.exe",
    "svchost.exe", "pythonw.exe", "python.exe", "zenlesszonezero.exe",
}


def detect(series, now_ts=None, floor=ANOMALY_FLOOR_GB, growth_gb=ANOMALY_GROWTH_GB,
           growth_ratio=ANOMALY_GROWTH_RATIO, redline=ANOMALY_REDLINE_GB,
           window_s=ANOMALY_WINDOW_S, never_kill=None):
    """series: {name_or_name|pid: [(ts_iso, gb), ...]}（按时间升序）。返回按严重度排序的异常列表。
    序列键可能带 pid（name|pid）：NEVER_KILL 校验与判定一律在剥掉 pid 的纯 name 上做，
    输出归并回纯 name——同名多 pid 实例取 current_gb 最大者（红线实例必然 >20GB，
    大于 suspect 区间 8-20GB，故比大小即兼取最严重）。"""
    never = {n.lower() for n in (never_kill or NEVER_KILL)}
    best_by_name = {}
    for key, points in series.items():
        name = key.split("|")[0]
        if name.lower() in never or len(points) < 2:
            continue
        cur_ts, cur_gb = points[-1]
        start_ts, start_gb = points[0]
        if cur_gb > redline:
            a = {"name": name, "level": "redline", "current_gb": round(cur_gb, 1),
                 "growth_gb": round(cur_gb - start_gb, 1),
                 "reason": f"{name} 当前 {cur_gb:.1f}GB 超过红线 {redline}GB"}
        else:
            growth = cur_gb - start_gb
            if not (cur_gb > floor and growth > growth_gb and growth > start_gb * growth_ratio):
                continue
            a = {"name": name, "level": "suspect", "current_gb": round(cur_gb, 1),
                 "growth_gb": round(growth, 1),
                 "reason": f"{name} 30 分钟内从 {start_gb:.1f}GB 涨到 {cur_gb:.1f}GB，疑似泄漏"}
        prev = best_by_name.get(name)
        if prev is None or a["current_gb"] > prev["current_gb"]:
            best_by_name[name] = a
    return sorted(best_by_name.values(), key=lambda a: -a["current_gb"])


def series_from_db(conn, collector="proctop", keys=("proctop1", "proctop2", "proctop3", "proctop4", "proctop5"),
                   window_s=ANOMALY_WINDOW_S):
    """从 metrics 表重建各进程窗口内序列（label 形如 'name|pid|cmdline'）。
    序列键为 'name|pid'：同名多实例（双开/轮换进出 Top5）各自独立成序，互不污染；
    key 限定 proctop 系列，防止 cpu_top_pct 等其它 key 的行混入序列。"""
    import time
    cutoff = time.strftime("%Y-%m-%dT%H:%M:%S", time.localtime(time.time() - window_s))
    ph = ",".join("?" for _ in keys)
    cur = conn.execute(
        f"SELECT ts, key, value, label FROM metrics WHERE collector=? AND key IN ({ph}) AND ts>=? ORDER BY ts",
        (collector, *keys, cutoff))
    series = {}
    for ts, key, value, label in cur.fetchall():
        parts = (label or "").split("|")
        name = parts[0]
        if not name:
            continue
        skey = f"{name}|{parts[1]}" if len(parts) > 1 and parts[1] else name
        series.setdefault(skey, []).append((ts, float(value)))
    return series

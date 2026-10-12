"""温和处置动作共用的频次熔断：kv 里 {key: [ts...]} 按对象分桶的滑动窗口。
与 kill_process 的分桶熔断同构但独立实现——kill 链是红线文件不回改，本模块只给 v1.6 新动作
（mem_trim / restart_service）用。"""
import json


def window_times(raw, key, now, window_s):
    """从 kv 原文解析指定 key 窗口内的时间戳列表。
    kv 缺失/损坏/非 dict/桶内容非列表一律视为无历史（fail-open 不阻断安全链）。
    返回 (完整 buckets dict, 该 key 窗口内时间戳列表)。"""
    buckets = {}
    if raw:
        try:
            d = json.loads(raw)
            if isinstance(d, dict):
                buckets = d
        except Exception:
            pass
    raw_times = buckets.get(key, [])
    if not isinstance(raw_times, list):
        raw_times = []
    times = []
    for t in raw_times:
        try:
            t = float(t)
        except (TypeError, ValueError):
            continue   # 非法元素逐个丢弃，不炸调用方
        if now - t < window_s:
            times.append(t)
    return buckets, times


def over_limit(kv_get, kv_key, key, limit, now, window_s):
    """查熔断：key 在窗口内次数 >= limit 返回 True（已熔断，调用方降级）。"""
    if not kv_get:
        return False
    _, times = window_times(kv_get(kv_key), key, now, window_s)
    return len(times) >= limit


def record(kv_get, kv_set, kv_key, key, now, window_s):
    """记账：追加一次时间戳并清掉空桶（kv 不膨胀）。"""
    buckets, times = window_times(kv_get(kv_key) if kv_get else None, key, now, window_s)
    times.append(now)
    buckets[key] = times
    buckets = {k: v for k, v in buckets.items() if v}
    if kv_set:
        kv_set(kv_key, json.dumps(buckets))

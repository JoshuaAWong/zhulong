"""白名单进程自动结束：安全链，任何一步不过即跳过/降级，绝不抛出未捕获异常。
顺序：NEVER_KILL 硬阻断 → 频次熔断 → 重读水位 → 存活时长 → 路径校验(fail-closed) → create_time 竞态防护 → kill → 记账"""
import json
import time

import psutil

from zhulong.core.anomaly import ANOMALY_REDLINE_GB, NEVER_KILL

GB = 1024 ** 3


def _find(procs, name):
    for p in procs:
        if (p.info.get("name") or "").lower() == name.lower():
            yield p


def _autokill_rule(process_name, cfg):
    """AUTOKILL 名单回落：元素允许字符串（仅按名匹配）或 dict（可带 path_contains）。
    阈值取异常红线 ANOMALY_REDLINE_GB（20GB）——能进名单的都是已判异常的进程。"""
    for e in getattr(cfg, "AUTOKILL", []):
        name = e["name"] if isinstance(e, dict) else e
        if name.lower() == process_name.lower():
            return {"name": process_name, "max_commit_gb": ANOMALY_REDLINE_GB,
                    "path_contains": e.get("path_contains") if isinstance(e, dict) else None}
    return None


def _load_kill_times(state, kv_get, now):
    """熔断计数：优先 kv 表（自动通道与面板通道共用同一份），无 kv 时退回 state 字典。"""
    raw = kv_get("kill_times") if kv_get else None
    times = []
    if raw:
        try:
            times = [float(t) for t in json.loads(raw)]
        except Exception:
            times = []   # kv 内容损坏视为无历史，不阻断安全链
    elif state is not None:
        times = state.get("kill_times", [])
    return [t for t in times if now - t < 600]


def _save_kill_times(times, state, kv_set):
    if kv_set:
        kv_set("kill_times", json.dumps(times))
    elif state is not None:
        state["kill_times"] = times


def run(process_name, cfg, state, psutil_mod=None, kv_get=None, kv_set=None):
    psutil_mod = psutil_mod or psutil
    now = time.time()
    # NEVER_KILL 入口硬阻断：防配置事故（即使管理员误把 explorer.exe 配进名单也杀不了）
    if process_name.lower() in NEVER_KILL:
        return {"status": "blocked:never_kill"}
    rule = next((w for w in cfg.WATCH_PROCESSES
                 if w["name"].lower() == process_name.lower()), None)
    if rule is None:
        rule = _autokill_rule(process_name, cfg)
    if rule is None:
        return {"status": "skipped:not_whitelisted"}

    # 频次熔断：10 分钟内超限 → 降级为仅通知（先于一切进程枚举）
    times = _load_kill_times(state, kv_get, now)
    if len(times) >= rule.get("max_kills_per_10min", 2):
        _save_kill_times(times, state, kv_set)
        return {"status": "degraded:frequency_cap"}

    # 重读水位：取同名进程中 commit 最大者（Windows 上 vms=PagefileUsage=commit，回退用）
    best, best_commit, create_time = None, -1.0, None
    for p in _find(psutil_mod.process_iter(["pid", "name", "create_time", "memory_info"]),
                   process_name):
        mi = p.info["memory_info"]
        commit = getattr(mi, "commit", None)
        if not commit:
            commit = mi.vms
        if commit > best_commit:
            best, best_commit = p, commit
            create_time = p.info["create_time"]
    if best is None:
        return {"status": "skipped:process_gone"}
    if best_commit / GB < rule["max_commit_gb"]:
        return {"status": "skipped:commit_below_threshold"}

    # 存活时长（避开启动峰值）
    if create_time is None or now - create_time < rule.get("min_age_s", 60):
        return {"status": "skipped:too_young"}

    # 路径校验（fail-closed：exe 读不到一律不放行，防同名假冒）
    path_contains = rule.get("path_contains")
    if path_contains:
        try:
            exe = best.exe()
        except psutil_mod.AccessDenied:
            return {"status": "degraded:access_denied"}
        if not exe or path_contains.lower() not in exe.lower():
            return {"status": "skipped:path_mismatch"}

    # create_time 竞态防护：kill 前重新读取，不一致 = PID 已被复用给别的进程
    try:
        fresh = best.create_time()
    except psutil_mod.NoSuchProcess:
        return {"status": "skipped:process_gone"}
    except psutil_mod.AccessDenied:
        return {"status": "degraded:access_denied"}
    if fresh != create_time:
        return {"status": "skipped:pid_reused"}

    try:
        best.kill()
    except psutil_mod.AccessDenied:
        return {"status": "degraded:access_denied"}
    except psutil_mod.NoSuchProcess:
        return {"status": "skipped:process_gone"}
    times.append(now)
    _save_kill_times(times, state, kv_set)
    return {"status": "killed", "pid": best.info["pid"],
            "commit_gb": round(best_commit / GB, 2)}


ACTION = {"name": "kill_process",
          "run": lambda params, cfg, state: run(params.get("process", ""), cfg, state,
                                                kv_get=state.get("_kv_get") if state else None,
                                                kv_set=state.get("_kv_set") if state else None)}

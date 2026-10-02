"""白名单进程自动结束：安全链，任何一步不过即跳过/降级，绝不抛出未捕获异常。
顺序：频次熔断 → 重读水位 → 存活时长 → 路径校验(fail-closed) → create_time 竞态防护 → kill → 记账"""
import time

import psutil

GB = 1024 ** 3


def _find(procs, name):
    for p in procs:
        if (p.info.get("name") or "").lower() == name.lower():
            yield p


def run(process_name, cfg, state, psutil_mod=None):
    psutil_mod = psutil_mod or psutil
    now = time.time()
    rule = next((w for w in cfg.WATCH_PROCESSES
                 if w["name"].lower() == process_name.lower()), None)
    if rule is None:
        return {"status": "skipped:not_whitelisted"}

    # 频次熔断：10 分钟内超限 → 降级为仅通知（先于一切进程枚举）
    times = [t for t in state.get("kill_times", []) if now - t < 600]
    if len(times) >= rule.get("max_kills_per_10min", 2):
        state["kill_times"] = times
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
    state["kill_times"] = times
    return {"status": "killed", "pid": best.info["pid"],
            "commit_gb": round(best_commit / GB, 2)}


ACTION = {"name": "kill_process",
          "run": lambda params, cfg, state: run(params.get("process", ""), cfg, state)}

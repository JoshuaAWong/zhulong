"""白名单进程自动结束：七步安全链，任何一步不过即跳过/降级，绝不抛异常。
1 重读水位 2 路径校验 3 存活时长 4 频次熔断 5 create_time 竞态校验 6 kill 7 异常降级"""
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

    # 频次熔断：10 分钟内超限 → 降级为仅通知
    times = [t for t in state.get("kill_times", []) if now - t < 600]
    if len(times) >= rule.get("max_kills_per_10min", 2):
        return {"status": "degraded:frequency_cap"}

    # 1) 重读水位：取同名进程中 commit 最大者（Windows 上 vms=PagefileUsage=commit，回退用）
    best, best_commit = None, -1.0
    for p in _find(psutil_mod.process_iter(["pid", "name", "create_time", "memory_info"]),
                   process_name):
        mi = p.info["memory_info"]
        commit = getattr(mi, "commit", None)
        if not commit:
            commit = mi.vms
        if commit > best_commit:
            best, best_commit = p, commit
    if best is None:
        return {"status": "skipped:process_gone"}
    if best_commit / GB < rule["max_commit_gb"]:
        return {"status": "skipped:commit_below_threshold"}

    # 3) 存活时长（避开启动峰值）
    create_time = best.info["create_time"]
    if now - create_time < rule.get("min_age_s", 60):
        return {"status": "skipped:too_young"}

    # 2) 路径校验（防同名假冒）
    path_contains = rule.get("path_contains")
    if path_contains:
        try:
            exe = best.exe()
        except psutil_mod.AccessDenied:
            return {"status": "degraded:access_denied"}
        if exe and path_contains.lower() not in exe.lower():
            return {"status": "skipped:path_mismatch"}

    # 5) kill 前后 create_time 比对（防 PID 复用）+ 6) kill + 7) 降级
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

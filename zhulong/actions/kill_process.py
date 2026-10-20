"""白名单进程自动结束：安全链，任何一步不过即跳过/降级，绝不抛出未捕获异常。
顺序：NEVER_KILL 硬阻断 → 频次熔断（按组分桶）→ 重读水位 → 存活时长 → 路径校验(fail-closed) → create_time 竞态防护 → kill → 记账
v1.7 池泄洪：同名多规则逐条独立判定（先触发先处置）；触发口径双轨——单进程
max_commit_gb（v1.3）或组聚合 group_commit_gb；victim_by=oldest 时从最老杀起、
不足 min_age_s 顺延次老。"""
import json
import time

import psutil

from zhulong import config as _config
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


def _load_kill_times(state, kv_get, now, bucket):
    """按组分桶的熔断计数：kv 里是 {bucket: [ts...]}（自动/面板通道共用）。
    旧格式（批次 A 的纯列表）与损坏内容一律视为无 per-process 历史（fail-open 不阻断安全链）。
    返回 (buckets, 该组窗口内时间戳列表)。"""
    raw = kv_get("kill_times") if kv_get else None
    buckets = {}
    if raw:
        try:
            d = json.loads(raw)
            if isinstance(d, dict):
                buckets = d
        except Exception:
            pass
    elif state is not None:
        d = state.get("kill_times", {})
        if isinstance(d, dict):
            buckets = d
    raw_times = buckets.get(bucket, [])
    if not isinstance(raw_times, list):
        raw_times = []   # kv 分桶内容非列表：视为无历史（fail-open）
    times = []
    for t in raw_times:
        try:
            t = float(t)
        except (TypeError, ValueError):
            continue   # 非法元素（None/字符串等）逐元素丢弃，不炸安全链
        if now - t < 600:
            times.append(t)
    return buckets, times


def _save_kill_times(buckets, bucket, times, state, kv_set):
    buckets[bucket] = times
    buckets = {k: v for k, v in buckets.items() if v}   # 清掉空桶，kv 不膨胀
    if kv_set:
        kv_set("kill_times", json.dumps(buckets))
    elif state is not None:
        state["kill_times"] = buckets


def _bucket_key(process_name, rule, cfg=None):
    """熔断分桶键：普通规则沿用进程名小写（v1.3 兼容，HYPHelper 熔断行为不变）；
    池泄洪规则用 watch_key——同名各组独立限速，额度不互挤。"""
    if rule.get("group_commit_gb") is not None:
        return _config.watch_key(rule).lower()
    return process_name.lower()


def _group_members(psutil_mod, process_name, rule):
    """枚举同名进程并按规则分桶。带 path_contains 的规则：exe 读不到 / 不含片段 /
    枚举竞态退出的成员一律不进组（fail-closed）。返回 (组成员列表, 同名进程总数)——
    组空时调用方据总数区分 process_gone（没有同名进程）与 path_mismatch（同名进程
    在但路径校验全部拦下，保持 v1.3 可观测语义）。"""
    pc = rule.get("path_contains")
    members, total = [], 0
    for p in _find(psutil_mod.process_iter(
            ["pid", "name", "create_time", "memory_info"]), process_name):
        mi = p.info.get("memory_info")
        if mi is None:
            continue   # 竞态：process_iter(attrs) 途中进程退出，attrs 取到 None
        total += 1   # 只计成功读到水位的候选者——竞态退出者不算"被路径拦下"
        commit = getattr(mi, "commit", None)
        if not commit:
            commit = mi.vms
        if pc:
            try:
                exe = p.exe()
            except (psutil_mod.AccessDenied, psutil_mod.NoSuchProcess):
                continue
            if not exe or pc.lower() not in exe.lower():
                continue
        members.append((p, commit / GB, p.info.get("create_time")))
    return members, total


def _pick_victim(members, rule, now):
    """受害者选择。victim_by=oldest：create_time 升序、不足 min_age_s 顺延次老，
    全部不足龄返回 (None, "all_too_young")；缺省（commit）：取 commit 最大者再查龄，
    不足龄返回 (None, "too_young")——v1.3 单进程口径语义保持不变。"""
    min_age = rule.get("min_age_s", 60)
    if rule.get("victim_by") == "oldest":
        for p, gb, ct in sorted(members, key=lambda m: m[2] if m[2] is not None else 0):
            if ct is not None and now - ct >= min_age:
                return p, gb, ct, None
        return None, None, None, "all_too_young"
    p, gb, ct = max(members, key=lambda m: m[1])
    if ct is None or now - ct < min_age:
        return None, None, None, "too_young"
    return p, gb, ct, None


def _run_one(process_name, rule, cfg, state, psutil_mod, kv_get, kv_set):
    """单条规则的完整安全链。返回结果 dict；skipped:* 可由调用方继续尝试下一条同名规则。"""
    now = time.time()
    # 频次熔断：10 分钟内超限 → 降级为仅通知（先于一切进程枚举）；按组分桶，
    # 一组的额度不挤占另一组
    bucket = _bucket_key(process_name, rule)
    buckets, times = _load_kill_times(state, kv_get, now, bucket)
    if len(times) >= rule.get("max_kills_per_10min", 2):
        _save_kill_times(buckets, bucket, times, state, kv_set)
        return {"status": "degraded:frequency_cap"}

    # 重读水位 + 路径分桶（带 path 规则在此一并完成校验，受害者无需再验路径）
    members, total = _group_members(psutil_mod, process_name, rule)
    if not members:
        if rule.get("path_contains") and total > 0:
            return {"status": "skipped:path_mismatch"}   # 同名进程在，路径校验全拦下
        return {"status": "skipped:process_gone"}
    commits = [gb for _, gb, _ in members]
    single_thr = rule.get("max_commit_gb")
    group_thr = rule.get("group_commit_gb")
    triggered = ((single_thr is not None and max(commits) >= single_thr)
                 or (group_thr is not None and sum(commits) >= group_thr))
    if not triggered:
        return {"status": "skipped:commit_below_threshold"}

    victim, v_commit, v_ct, skip = _pick_victim(members, rule, now)
    if victim is None:
        return {"status": f"skipped:{skip}"}
    # 路径校验已在分桶时对每个成员完成（fail-closed：exe 读不到不进组）；
    # 无 path_contains 的规则本就不验路径（v1.3 语义）

    # create_time 竞态防护：kill 前重新读取，不一致 = PID 已被复用给别的进程
    try:
        fresh = victim.create_time()
    except psutil_mod.NoSuchProcess:
        return {"status": "skipped:process_gone"}
    except psutil_mod.AccessDenied:
        return {"status": "degraded:access_denied"}
    if fresh != v_ct:
        return {"status": "skipped:pid_reused"}

    try:
        victim.kill()
    except psutil_mod.AccessDenied:
        return {"status": "degraded:access_denied"}
    except psutil_mod.NoSuchProcess:
        return {"status": "skipped:process_gone"}
    times.append(now)
    _save_kill_times(buckets, bucket, times, state, kv_set)
    return {"status": "killed", "pid": victim.info["pid"],
            "commit_gb": round(v_commit, 2)}


def run(process_name, cfg, state, psutil_mod=None, kv_get=None, kv_set=None):
    psutil_mod = psutil_mod or psutil
    # NEVER_KILL 入口硬阻断：防配置事故（即使管理员误把 explorer.exe 配进名单也杀不了）
    if process_name.lower() in NEVER_KILL:
        return {"status": "blocked:never_kill"}
    rules = [w for w in cfg.WATCH_PROCESSES
             if w["name"].lower() == process_name.lower()]
    if not rules:
        rule = _autokill_rule(process_name, cfg)
        if rule is None:
            return {"status": "skipped:not_whitelisted"}
        rules = [rule]
    # 同名多规则逐条判定：处置成功/被安全链拦截即返回；全部 skipped 时保留第一条
    # （配置顺序=主规则）的 skip 原因，不拿后续规则的组空/路径结果覆盖诊断信息
    first_skip = None
    for rule in rules:
        result = _run_one(process_name, rule, cfg, state, psutil_mod, kv_get, kv_set)
        if not result["status"].startswith("skipped:"):
            return result
        if first_skip is None:
            first_skip = result
    return first_skip or {"status": "skipped:not_whitelisted"}


ACTION = {"name": "kill_process",
          "run": lambda params, cfg, state: run(params.get("process", ""), cfg, state,
                                                kv_get=state.get("_kv_get") if state else None,
                                                kv_set=state.get("_kv_set") if state else None)}

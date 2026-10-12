"""限流器：按配置给目标进程"戴镣铐"（优先级 + 相关性绑核）——全部进程运行时属性，
不写注册表、不动对方文件，天然可逆。
规则双层：config.THROTTLE_DEFAULTS 出厂规则（只读）；data/throttle_override.json 用户覆盖（优先）。
开关：kv throttle_enabled（托盘/面板动作通道写）。已施加清单：kv throttle_applied
（restore 按清单还原而非按现行规则，override 删规则后也不漏网）。"""
import json
import os
import time

import psutil

from zhulong.core import storage

PRIORITY = {"normal": psutil.NORMAL_PRIORITY_CLASS, "below_normal": psutil.BELOW_NORMAL_PRIORITY_CLASS}


def _ts():
    return time.strftime("%Y-%m-%dT%H:%M:%S")


def rules(cfg, override_path=None):
    """生效规则：override 文件存在用覆盖，否则出厂规则。"""
    ov = override_path or (cfg.DATA_DIR / "throttle_override.json")
    if ov.exists():
        try:
            return json.loads(ov.read_text(encoding="utf-8"))["rules"]
        except Exception:
            pass   # 覆盖文件损坏回落出厂，不炸调度
    return cfg.THROTTLE_DEFAULTS["rules"]


def cores_of(spec, cpu_count):
    if isinstance(spec, str) and spec.startswith("tail:"):
        k = max(1, min(int(spec.split(":")[1]), cpu_count))
        return list(range(cpu_count - k, cpu_count))
    return sorted({int(c) for c in spec if 0 <= int(c) < cpu_count})


def assign_cores(rule_list, cpu_count):
    """解析各规则的核组（v1.6）。tail:N / 核号列表独立解析（多规则共享同组核，即"叠核"）；
    spread:N 按规则顺序从最高核向下各切连续 N 核块——规则间不重叠，专治多规则叠核。
    游标下限钳在 cpu_count//2（不侵入前半核区=P 核区；小 CPU 上超量时回落共享下限块）。
    分配只依赖规则列表顺序 → 确定性 → 幂等不变。"""
    resolved = []
    cursor = cpu_count
    floor = cpu_count // 2
    for r in rule_list:
        spec = r.get("cores", "tail:2")
        if isinstance(spec, str) and spec.startswith("spread:"):
            k = max(1, min(int(spec.split(":")[1]), cpu_count))
            lo = max(floor, cursor - k)
            cores = list(range(lo, cursor))
            if not cores:   # 前序规则已吃到下限：回落共享下限块（宁可共享不再下探）
                cores = list(range(floor, min(floor + k, cpu_count)))
            cursor = lo
        else:
            cores = cores_of(spec, cpu_count)
        resolved.append(cores)
    return resolved


def validate_rules(rule_list):
    """override.json schema 校验：name 非空字符串；priority ∈ PRIORITY 白名单；
    cores 合法（"tail:N" / "spread:N" / 核号列表）。非法规则跳过（调用方记一次事件），
    不再让 ValueError/TypeError 炸掉整轮限流。返回 (合法规则, 非法规则)。"""
    good, bad = [], []
    for r in rule_list or []:
        ok = isinstance(r, dict) and isinstance(r.get("name"), str) and r["name"] \
            and r.get("priority", "below_normal") in PRIORITY
        if ok:
            cores = r.get("cores", "tail:2")
            ok = (isinstance(cores, str)
                  and (cores.startswith("tail:") or cores.startswith("spread:"))
                  and cores.split(":", 1)[1].isdigit() and int(cores.split(":", 1)[1]) >= 1) \
                 or (isinstance(cores, list) and cores
                     and all(isinstance(c, int) and not isinstance(c, bool) for c in cores))
        (good if ok else bad).append(r)
    return good, bad


def apply_if_due(conn, cfg, state, psutil_mod=None):
    """分频幂等应用：已是目标态跳过；状态变化才记事件；永不处理自身进程。"""
    pm = psutil_mod or psutil
    state["throttle_cycle"] = state.get("throttle_cycle", 0) + 1
    if state["throttle_cycle"] % getattr(cfg, "THROTTLE_INTERVAL_CYCLES", 4) != 0:
        return
    if storage.kv_get(conn, "throttle_enabled") != "1":
        enabled = storage.kv_get(conn, "throttle_enabled")
        if enabled is None:
            enabled = "1" if getattr(cfg, "THROTTLE_DEFAULTS", {}).get("enabled") else "0"
        if enabled != "1":
            return
    applied = json.loads(storage.kv_get(conn, "throttle_applied") or "{}")
    n = pm.cpu_count()
    # 规则一次取值（此前在 process_iter 循环体内——200 进程 = 读 200 次 override.json）
    good, bad = validate_rules(rules(cfg))
    if bad:   # 非法规则跳过并记一次事件（内容不变不重复记，防每 2 分钟刷屏）
        marker = json.dumps(bad, sort_keys=True, ensure_ascii=False, default=str)
        if state.get("_bad_rules") != marker:
            state["_bad_rules"] = marker
            storage.insert_event(conn, _ts(), "throttle", "skip",
                                 json.dumps({"reason": "invalid_rule", "rules": bad},
                                            ensure_ascii=False, default=str))
    wanted = {}
    for ri, r in enumerate(good):
        for p in pm.process_iter(["pid", "name"]):
            if r["name"].lower() == (p.info["name"] or "").lower():
                wanted[p.info["pid"]] = (p, ri)
    resolved = assign_cores(good, n)   # spread 需跨规则分配，故按规则序统一解析核组
    skipped = []
    for pid, (proc, ri) in wanted.items():
        if pid == os.getpid():
            continue
        rule = good[ri]
        prio = PRIORITY.get(rule.get("priority", "below_normal"))
        cores = resolved[ri]
        try:
            if proc.nice() == prio and sorted(proc.cpu_affinity()) == cores:
                continue   # 幂等：已是目标态
            proc.nice(prio)
            proc.cpu_affinity(cores)
            applied[proc.name()] = {"priority": rule.get("priority", "below_normal"), "cores": cores}
            storage.insert_event(conn, _ts(), "throttle", "apply",
                                 json.dumps({"process": proc.name(), "pid": pid, "cores": cores},
                                            ensure_ascii=False))
        except pm.NoSuchProcess:
            continue
        except pm.AccessDenied:
            skipped.append(proc.name())   # 提权/自保护进程（如电脑管家）：不再静默
    # 状态变化才记：denied 集合与上轮相同不重复记——自保护进程（如 QQPCTray）永远
    # AccessDenied，每轮（2 分钟）记一条会变成每天 720 条的告警风暴（同款 _bad_rules 模式）
    cur_denied = sorted(skipped)
    if cur_denied != state.get("_last_denied", []):
        state["_last_denied"] = cur_denied
        if cur_denied:
            storage.insert_event(conn, _ts(), "throttle", "skip",
                                 json.dumps({"reason": "access_denied", "processes": cur_denied},
                                            ensure_ascii=False))
    storage.kv_set(conn, "throttle_applied", json.dumps(applied, ensure_ascii=False))


def restore(conn, psutil_mod=None, state=None):
    """按已施加清单全量还原（normal + 全部核），清空清单。返回还原进程数。
    state 可选：传入则 denied 集合去重（同 apply）；不传（退出/手动一次性调用）则照记。"""
    pm = psutil_mod or psutil
    applied = json.loads(storage.kv_get(conn, "throttle_applied") or "{}")
    if not applied:
        return 0
    all_cores = list(range(pm.cpu_count()))
    done = 0
    skipped = []
    for p in pm.process_iter(["pid", "name"]):
        name = p.info["name"] or ""
        if name in applied and p.info["pid"] != os.getpid():
            try:
                p.nice(pm.NORMAL_PRIORITY_CLASS)
                p.cpu_affinity(all_cores)
                done += 1
                storage.insert_event(conn, _ts(), "throttle", "restore",
                                     json.dumps({"process": name}, ensure_ascii=False))
            except pm.NoSuchProcess:
                continue
            except pm.AccessDenied:
                skipped.append(name)   # 自保护进程还原被拒：记一次 skip 事件（此前静默）
    if skipped:
        cur_denied = sorted(skipped)
        if state is None or cur_denied != state.get("_last_denied", []):
            if state is not None:
                state["_last_denied"] = cur_denied
            storage.insert_event(conn, _ts(), "throttle", "skip",
                                 json.dumps({"reason": "access_denied", "processes": cur_denied,
                                             "op": "restore"}, ensure_ascii=False))
    storage.kv_set(conn, "throttle_applied", "{}")
    return done

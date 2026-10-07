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
    wanted = {}
    for p in pm.process_iter(["pid", "name"]):
        name = (p.info["name"] or "").lower()
        for r in rules(cfg):
            if r["name"].lower() == name:
                wanted[p.info["pid"]] = (p, r)
    for pid, (proc, rule) in wanted.items():
        if pid == os.getpid():
            continue
        prio = PRIORITY.get(rule.get("priority", "below_normal"))
        cores = cores_of(rule.get("cores", "tail:2"), n)
        try:
            if proc.nice() == prio and sorted(proc.cpu_affinity()) == cores:
                continue   # 幂等：已是目标态
            proc.nice(prio)
            proc.cpu_affinity(cores)
            applied[proc.name()] = {"priority": rule.get("priority", "below_normal"), "cores": cores}
            storage.insert_event(conn, _ts(), "throttle", "apply",
                                 json.dumps({"process": proc.name(), "pid": pid, "cores": cores},
                                            ensure_ascii=False))
        except (pm.NoSuchProcess, pm.AccessDenied):
            continue
    storage.kv_set(conn, "throttle_applied", json.dumps(applied, ensure_ascii=False))


def restore(conn, psutil_mod=None):
    """按已施加清单全量还原（normal + 全部核），清空清单。返回还原进程数。"""
    pm = psutil_mod or psutil
    applied = json.loads(storage.kv_get(conn, "throttle_applied") or "{}")
    if not applied:
        return 0
    all_cores = list(range(pm.cpu_count()))
    done = 0
    for p in pm.process_iter(["pid", "name"]):
        name = p.info["name"] or ""
        if name in applied and p.info["pid"] != os.getpid():
            try:
                p.nice(pm.NORMAL_PRIORITY_CLASS)
                p.cpu_affinity(all_cores)
                done += 1
                storage.insert_event(conn, _ts(), "throttle", "restore",
                                     json.dumps({"process": name}, ensure_ascii=False))
            except (pm.NoSuchProcess, pm.AccessDenied):
                continue
    storage.kv_set(conn, "throttle_applied", "{}")
    return done

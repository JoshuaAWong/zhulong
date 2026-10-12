"""内存回收巡检：对白名单后台进程调 EmptyWorkingSet 挤出物理内存页（进程不中断不丢数据，
下次访问从待备列表快速调回）。镜像 throttle 成熟模式：kv 开关 + 分频幂等 + 事件审计 + 永不处理自身 PID。
如实分工：trim 只降物理内存（working set），不减 commit——commit 泄漏仍走 WATCH/AUTOKILL kill 链；
页面文件由系统自动回收，烛龙不做任何页面文件操作。
安全链（镜像 kill 链序）：名单级硬阻断 → 频次熔断 → 重读水位 → 关键进程/前台阻断(fail-closed)
→ 存活时长 → 路径校验(fail-closed) → create_time 竞态防护 → EmptyWorkingSet → 记账。"""
import ctypes
import json
import logging
import os
import time

import psutil

from zhulong.core import freq_cap, protect, storage

LOG = logging.getLogger("zhulong")
GB = 1024 ** 3

_k32 = ctypes.WinDLL("kernel32", use_last_error=True)
_psapi = ctypes.WinDLL("psapi", use_last_error=True)
_PROCESS_SET_QUOTA = 0x0100
_PROCESS_QUERY_LIMITED_INFORMATION = 0x1000


def _ts():
    return time.strftime("%Y-%m-%dT%H:%M:%S")


def _trim_handle(pid):
    """OpenProcess(最小权限) + EmptyWorkingSet + CloseHandle。返回 (成功?, winerror)。"""
    h = _k32.OpenProcess(_PROCESS_SET_QUOTA | _PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
    if not h:
        return False, ctypes.get_last_error()
    try:
        if not _psapi.EmptyWorkingSet(h):
            return False, ctypes.get_last_error()
        return True, 0
    finally:
        _k32.CloseHandle(h)


def trim_once(process_name, cfg, state, psutil_mod=None, kv_get=None, kv_set=None):
    """对单个白名单进程执行一次 trim 安全链。面板手动与巡检共用同一条链与熔断计数。"""
    pm = psutil_mod or psutil
    now = time.time()
    rule = next((w for w in cfg.MEM_TRIM.get("processes", [])
                 if w["name"].lower() == process_name.lower()), None)
    if rule is None:
        return {"status": "skipped:not_whitelisted"}
    # 名单级硬阻断：安全软件/关键进程（无需枚举，先于一切——镜像 kill 入口硬阻断）
    hit, reason = protect.name_blocked(process_name.lower())
    if hit:
        return {"status": f"blocked:{reason}"}
    # 频次熔断：超限降级为不动手
    if freq_cap.over_limit(kv_get, "trim_times", process_name.lower(),
                           rule.get("max_trims_per_10min", 1), now, 600):
        return {"status": "degraded:frequency_cap"}
    # 重读水位：同名进程取 working set 最大者（竞态下 memory_info 为 None 的实例跳过）
    best, best_ws, create_time = None, -1.0, None
    for p in pm.process_iter(["pid", "name", "create_time", "memory_info"]):
        if p.info["pid"] == os.getpid():
            continue   # 永不处理自身（双保险，名单外再加一道）
        if (p.info.get("name") or "").lower() != process_name.lower():
            continue
        mi = p.info.get("memory_info")
        if mi is None:
            continue
        ws = getattr(mi, "wset", 0) or 0
        if ws > best_ws:
            best, best_ws, create_time = p, ws, p.info["create_time"]
    if best is None:
        return {"status": "skipped:process_gone"}
    if best_ws / GB < rule.get("min_ws_gb", 2.0):
        return {"status": "skipped:ws_below_threshold"}
    # 关键进程识别（SYSTEM 账户/系统路径/前台窗口，fail-closed）
    crit, reason = protect.is_critical(process_name.lower(), best)
    if crit:
        return {"status": f"blocked:{reason}"}
    # 存活时长（刚启动的进程工作集还没稳定）
    if create_time is None or now - create_time < rule.get("min_age_s", 300):
        return {"status": "skipped:too_young"}
    # 路径校验（fail-closed：exe 读不到一律不放行，防同名假冒）
    path_contains = rule.get("path_contains")
    if path_contains:
        try:
            exe = best.exe()
        except pm.AccessDenied:
            return {"status": "degraded:access_denied"}
        if not exe or path_contains.lower() not in exe.lower():
            return {"status": "skipped:path_mismatch"}
    # create_time 竞态防护：trim 前重读，不一致 = PID 已被复用
    try:
        fresh = best.create_time()
    except pm.NoSuchProcess:
        return {"status": "skipped:process_gone"}
    except pm.AccessDenied:
        return {"status": "degraded:access_denied"}
    if fresh != create_time:
        return {"status": "skipped:pid_reused"}
    ok, err = _trim_handle(best.info["pid"])
    if not ok:
        return {"status": "degraded:access_denied", "winerror": err}
    freq_cap.record(kv_get, kv_set, "trim_times", process_name.lower(), now, 600)
    after = None
    try:
        after = round(best.memory_info().wset / GB, 2)
    except Exception:
        pass   # 回读失败不影响结果（进程可能刚退出）
    return {"status": "trimmed", "pid": best.info["pid"],
            "ws_gb_before": round(best_ws / GB, 2), "ws_gb_after": after}


def apply_if_due(conn, cfg, state, psutil_mod=None):
    """分频巡检：总开关 → 物理内存压力门槛 → 逐目标 trim_once（事件按状态变化记录）。"""
    pm = psutil_mod or psutil
    mt = cfg.MEM_TRIM
    state["memtrim_cycle"] = state.get("memtrim_cycle", 0) + 1
    if state["memtrim_cycle"] % mt.get("interval_cycles", 2) != 0:
        return
    enabled = storage.kv_get(conn, "autoclean_enabled")
    if enabled is None:
        enabled = "1" if mt.get("enabled") else "0"
    if enabled != "1":
        return
    # 压力门槛：物理内存不紧张时整轮跳过（空闲时 Windows 自己管理待备列表，不折腾）
    try:
        if pm.virtual_memory().percent < mt.get("min_mem_percent", 60):
            return
    except Exception:
        return   # 内存占用读不到：按无压力处理，本轮不动手
    kv_get = lambda k: storage.kv_get(conn, k)
    kv_set = lambda k, v: storage.kv_set(conn, k, v)
    last = state.setdefault("_last_memtrim", {})
    for w in mt.get("processes", []):
        try:
            result = trim_once(w["name"], cfg, state, psutil_mod=pm,
                               kv_get=kv_get, kv_set=kv_set)
        except Exception:
            LOG.exception("内存回收巡检异常（%s）", w["name"])
            result = {"status": "degraded:exception"}
        status = result.get("status", "")
        # trimmed 每次都记（熔断限频本身稀有）；其余状态只在变化时记——防每分钟刷屏（B1 教训）
        if status == "trimmed" or status != last.get(w["name"].lower()):
            storage.insert_event(conn, _ts(), "memtrim", "trim",
                                 json.dumps({"process": w["name"], "result": result},
                                            ensure_ascii=False))
        last[w["name"].lower()] = status

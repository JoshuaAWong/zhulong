"""泄漏服务温和重启：stop → start。服务宿主进程泄漏的正确处置是重启服务而非杀进程——
杀了会连坐整组同宿主服务（实施后记 WmiPrvSE 根治实录的结论）。
安全链：白名单 → SERVICES_NEVER_RESTART 硬阻断 → 频次熔断（发起即记账，防反复敲打）
→ 存在性/状态确认 → stop 轮询等待 → start 轮询等待（失败重试 2 次，仍失败记 degraded:start_failed）。
触发：rules._restart_rules 动态规则（params 带 auto=True，受总开关闸）；面板手动不受总开关限。"""
import time

import psutil

from zhulong.core import freq_cap

# 绝对不动的 Windows 关键服务（重启会连坐系统组件甚至蓝屏）——误配白名单也动不了
SERVICES_NEVER_RESTART = {"rpcss", "dcomlaunch", "plugplay", "power", "lsm", "samss"}

STOP_TIMEOUT_S = 30
START_TIMEOUT_S = 30
START_RETRIES = 2
RETRY_WAIT_S = 5


def _wait_status(svc, want, timeout_s, monotonic=time.monotonic, sleep=time.sleep):
    """轮询等待服务到目标状态；超时返回 False（最后再查一次兜底）。"""
    deadline = monotonic() + timeout_s
    while monotonic() < deadline:
        try:
            if svc.status() == want:
                return True
        except psutil.NoSuchService:
            return False
        sleep(1)
    try:
        return svc.status() == want
    except psutil.NoSuchService:
        return False


def run(params, cfg, state, psutil_mod=None, kv_get=None, kv_set=None):
    pm = psutil_mod or psutil
    now = time.time()
    service = params.get("service", "")
    if not service:
        return {"status": "skipped:no_service"}
    # 总开关只闸自动通道（规则 params 带 auto=True）；面板手动优先于开关
    if params.get("auto") and kv_get and kv_get("autoclean_enabled") == "0":
        return {"status": "skipped:disabled"}
    rule = next((w for w in getattr(cfg, "RESTART_SERVICES", [])
                 if w["name"].lower() == service.lower()), None)
    if rule is None:
        return {"status": "skipped:not_whitelisted"}
    if service.lower() in SERVICES_NEVER_RESTART:
        return {"status": "blocked:never_restart"}
    # 频次熔断：发起即记账（防 start 失败后规则每轮冷却到期又敲打半死的服务）
    cap = rule.get("max_restarts_per_hour", 1)
    if freq_cap.over_limit(kv_get, "restart_times", service.lower(), cap, now, 3600):
        return {"status": "degraded:frequency_cap"}
    freq_cap.record(kv_get, kv_set, "restart_times", service.lower(), now, 3600)
    try:
        svc = pm.win_service_get(service)
    except Exception:
        return {"status": "skipped:not_found"}
    try:
        if svc.status() != "running":
            return {"status": "skipped:not_running"}
        svc.stop()
    except pm.AccessDenied:
        return {"status": "degraded:access_denied"}
    except Exception:
        return {"status": "degraded:stop_failed"}
    if not _wait_status(svc, "stopped", STOP_TIMEOUT_S):
        return {"status": "degraded:stop_timeout"}   # 服务多半还在（缓慢停止中），不再强动
    # start + 补偿重试：仍失败则服务躺着，事件面板可见、人工拉起
    for attempt in range(1 + START_RETRIES):
        try:
            svc.start()
        except pm.AccessDenied:
            return {"status": "degraded:access_denied"}
        except Exception:
            pass   # 启动报错（依赖未就绪等）：等一轮再试
        if _wait_status(svc, "running", START_TIMEOUT_S):
            return {"status": "restarted", "service": service}
        if attempt < START_RETRIES:
            time.sleep(RETRY_WAIT_S)
    return {"status": "degraded:start_failed"}


ACTION = {"name": "restart_service",
          "run": lambda params, cfg, state: run(params, cfg, state,
                                                kv_get=state.get("_kv_get") if state else None,
                                                kv_set=state.get("_kv_set") if state else None)}

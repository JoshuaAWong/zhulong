"""规则定义：数据式 dict，由 core.engine 评估。加规则=加一个 dict。"""
from zhulong import config


def _commit_high(ctx):
    v = ctx.get("memory.commit_percent")
    return v is not None and v >= config.HIGH_WATER["enter"]


def _commit_high_reset(ctx):
    v = ctx.get("memory.commit_percent")
    return v is not None and v <= config.HIGH_WATER["exit"]


def _has_anomaly(ctx):
    return bool(getattr(ctx, "anomalies", None))


def _single_core_hot(ctx):
    """单核饱和：最忙核接近打满而总占用不高——多进程挤核的特征（总值不高但一样卡）。"""
    mx = ctx.get("cpu.cpu_max_core")
    avg = ctx.get("cpu.cpu_percent")
    s = config.SINGLE_CORE
    return (mx is not None and avg is not None
            and mx >= s["enter"] and avg < s["avg_below"])


RULES = [
    {
        "name": "anomaly_leak",
        "condition": _has_anomaly,
        "for_seconds": 0,
        "cooldown_seconds": config.COOLDOWN_S,
        "actions": ["toast"],   # 默认仅告警；自动杀见 AUTOKILL 动态规则（下）
        "params": {"toast_title": "烛龙：检测到异常进程",
                   "toast_body": "有进程疑似内存泄漏，详情见面板大户榜与朱批记事。"},
    },
    {
        "name": "commit_high",
        "condition": _commit_high,
        "reset_below": _commit_high_reset,   # 迟滞：触发态须 ≤80% 才解除
        "for_seconds": config.HIGH_WATER["for_seconds"],
        "cooldown_seconds": config.COOLDOWN_S,
        "actions": ["toast"],
        "params": {"toast_title": "烛龙：虚拟内存水位过高",
                   "toast_body": "commit 超过 85% 已持续 90 秒，点击查看面板定位占用大户。"},
    },
    {
        "name": "single_core_hot",
        "condition": _single_core_hot,
        "for_seconds": config.SINGLE_CORE["for_seconds"],
        "cooldown_seconds": config.COOLDOWN_S,
        "actions": ["toast"],   # 只告警不自动绑核（非白名单进程不动，处置留给限流错峰）
        "params": {"toast_title": "烛龙：单核饱和",
                   "toast_body": "最忙核心接近打满而总占用不高（多进程挤核），见面板查看大户；"
                                 "后台进程错峰绑核可分散压力。"},
    },
]

# AUTOKILL 动态规则：名单内进程一旦判定异常即自动终结（仍走七步安全链）
# 名单元素允许字符串或 dict（{"name", "path_contains"?}），此处只取名字
def _autokill_rules():
    rules = []
    for entry in getattr(config, "AUTOKILL", []):
        name = entry["name"] if isinstance(entry, dict) else entry
        rules.append({
            "name": f"autokill_{name}",
            "condition": lambda ctx, n=name: any(a["name"].lower() == n.lower()
                                                 for a in getattr(ctx, "anomalies", [])),
            "for_seconds": 0,
            "cooldown_seconds": config.COOLDOWN_S,
            "actions": ["kill_process", "toast"],
            "params": {"process": name,
                       "toast_title": "烛龙：已自动终结异常进程",
                       "toast_body": f"{name} 疑似泄漏，已自动处置，详情见面板事件。"},
        })
    return rules


RULES.extend(_autokill_rules())


# 进程池泄洪动态规则（v1.7）：配了 group_commit_gb 的 WATCH 规则各生成一条。
# 双保险触发：组聚合 ≥ 阈值 且 系统 commit 水位 ≥ HIGH_WATER.enter（无压力不折腾）；
# for_seconds 沿用高水位持续判定，防瞬时尖峰。kill_process 按名取全部同名规则逐条
# 判定（params 不带 watch_key，水位重读时自然定位到超限的组）。
def _pool_high_rules():
    rules = []
    for w in getattr(config, "WATCH_PROCESSES", []):
        if w.get("group_commit_gb") is None:
            continue
        wk = config.watch_key(w)
        thr = w["group_commit_gb"]
        display = w.get("display_name", w["name"])
        rules.append({
            "name": f"pool_high_{wk}",
            "condition": lambda ctx, k=wk, t=thr: (
                (ctx.get(f"pool.{k}_gb") or 0) >= t
                and (ctx.get("memory.commit_percent") or 0) >= config.HIGH_WATER["enter"]
            ),
            "for_seconds": config.HIGH_WATER["for_seconds"],
            "cooldown_seconds": config.COOLDOWN_S,
            "actions": ["kill_process", "toast"],
            "params": {"process": w["name"],
                       "toast_title": "烛龙：进程池泄洪",
                       "toast_body": f"{display} 聚合超 {thr}GB 且系统水位高，"
                                     f"自动处置最老进程，详情见面板事件。"},
        })
    return rules


RULES.extend(_pool_high_rules())


# 泄漏服务温和重启动态规则：trigger_processes 里任一进程被判异常即重启对应宿主服务
# （镜像 AUTOKILL 模式；params 带 auto=True，动作内受总开关 kv autoclean_enabled 闸）
def _restart_rules():
    rules = []
    for entry in getattr(config, "RESTART_SERVICES", []):
        svc = entry["name"]
        procs = tuple(p.lower() for p in entry.get("trigger_processes", []))
        display = entry.get("display_name", svc)
        rules.append({
            "name": f"restart_{svc}",
            "condition": lambda ctx, ps=procs: any(a["name"].lower() in ps
                                                   for a in getattr(ctx, "anomalies", [])),
            "for_seconds": 0,
            "cooldown_seconds": config.COOLDOWN_S,
            "actions": ["restart_service", "toast"],
            "params": {"service": svc, "auto": True,
                       "toast_title": "烛龙：已温和重启泄漏服务",
                       "toast_body": f"{display} 宿主进程疑似泄漏，已自动重启服务，详情见面板事件。"},
        })
    return rules


RULES.extend(_restart_rules())

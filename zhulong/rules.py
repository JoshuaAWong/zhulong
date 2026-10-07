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
]

# AUTOKILL 动态规则：名单内进程一旦判定异常即自动终结（仍走七步安全链）
def _autokill_rules():
    rules = []
    for name in getattr(config, "AUTOKILL", []):
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

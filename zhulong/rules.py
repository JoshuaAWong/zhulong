"""规则定义：数据式 dict，由 core.engine 评估。加规则=加一个 dict。"""
from zhulong import config


def _commit_high(ctx):
    v = ctx.get("memory.commit_percent")
    return v is not None and v >= config.HIGH_WATER["enter"]


def _commit_high_reset(ctx):
    v = ctx.get("memory.commit_percent")
    return v is not None and v <= config.HIGH_WATER["exit"]


def _hyphelper(ctx):
    rule = next((w for w in config.WATCH_PROCESSES
                 if w["name"].lower() == "hyphelper.exe"), None)
    gb = ctx.process_commit_gb("HYPHelper.exe")
    return gb is not None and rule is not None and gb > rule["max_commit_gb"]


RULES = [
    {
        "name": "hyphelper_leak",
        "condition": _hyphelper,
        "for_seconds": 0,
        "cooldown_seconds": config.COOLDOWN_S,
        "actions": ["kill_process", "toast"],
        "params": {"process": "HYPHelper.exe",
                   "toast_title": "烛龙：已自动结束泄漏进程",
                   "toast_body": "HYPHelper.exe 占用超过阈值，已执行自动处置，详情见面板事件。"},
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

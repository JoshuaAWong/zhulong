from zhulong.core.engine import Engine, MetricCtx
from zhulong.rules import RULES


def make_engine():
    store = {}
    return Engine(kv_get=lambda k: store.get(k), kv_set=lambda k, v: store.__setitem__(k, v)), store


def ctx(percent=50, hhelper=1.0):
    return MetricCtx({("memory", "commit_percent"): percent},
                     {"hyphelper.exe": hhelper})


def by_name(rules, name):
    return [r for r in rules if r["name"] == name]


# 本地泄漏测试规则：替代已下线的 hyphelper_leak（引擎锁存/静音/幻影回归与生产规则名解耦）
LEAK_RULE = {
    "name": "leak_test",
    "condition": lambda ctx: (ctx.process_commit_gb("HYPHelper.exe") or 0) > 15,
    "for_seconds": 0,
    "cooldown_seconds": 600,
    "actions": ["kill_process", "toast"],
    "params": {"process": "HYPHelper.exe"},
}


def main():
    # ---- 窗口：commit_high 需连续 90s ----
    eng, _ = make_engine()
    r = by_name(RULES, "commit_high")[0]
    fired = eng.evaluate([r], ctx(percent=90), 0.0)
    assert fired == [], "窗口未满不应触发"
    eng.evaluate([r], ctx(percent=90), 30.0)
    eng.evaluate([r], ctx(percent=90), 60.0)
    fired = eng.evaluate([r], ctx(percent=90), 90.0)
    assert len(fired) == 1 and fired[0]["rule"] == "commit_high" and not fired[0]["muted"], fired

    # ---- 冷却：触发后 600s 内不再触发 ----
    fired = eng.evaluate([r], ctx(percent=90), 100.0)
    assert fired == [], "冷却期内不应触发"
    fired = eng.evaluate([r], ctx(percent=90), 90.0 + 601)
    assert len(fired) == 1, "冷却结束后应可再触发"

    # ---- 窗口中断重置 ----
    eng2, _ = make_engine()
    eng2.evaluate([r], ctx(percent=90), 0.0)
    eng2.evaluate([r], ctx(percent=90), 30.0)
    eng2.evaluate([r], ctx(percent=50), 60.0)   # 中断
    eng2.evaluate([r], ctx(percent=90), 90.0)
    fired = eng2.evaluate([r], ctx(percent=90), 100.0)
    assert fired == [], "中断后窗口应重新计时"

    # ---- 迟滞：触发态下 80<x<85 仍算成立，≤80 解除 ----
    eng3, _ = make_engine()
    rh = by_name(RULES, "commit_high")[0]
    eng3.evaluate([rh], ctx(percent=90), 0.0)
    eng3.evaluate([rh], ctx(percent=90), 30.0)
    eng3.evaluate([rh], ctx(percent=90), 60.0)
    eng3.evaluate([rh], ctx(percent=90), 90.0)          # 触发，迟滞态开
    assert eng3.evaluate([rh], ctx(percent=83), 100.0) == []   # 冷却期
    # 冷却后、值 83（>80 迟滞未解除）：条件仍视为成立→立即再触发
    eng3.cooldown_until_by_name["commit_high"] = 0
    fired = eng3.evaluate([rh], ctx(percent=83), 700.0)
    assert len(fired) == 1, "迟滞态下 83% 应视为持续成立"
    # 值降到 79：解除迟滞态
    eng3.cooldown_until_by_name["commit_high"] = 0
    assert eng3.evaluate([rh], ctx(percent=79), 800.0) == []
    eng3.cooldown_until_by_name["commit_high"] = 0
    assert eng3.evaluate([rh], ctx(percent=90), 900.0) == []   # 重新开始窗口
    eng3.cooldown_until_by_name["commit_high"] = 0
    eng3.evaluate([rh], ctx(percent=90), 930.0)
    eng3.cooldown_until_by_name["commit_high"] = 0
    eng3.evaluate([rh], ctx(percent=90), 960.0)
    eng3.cooldown_until_by_name["commit_high"] = 0
    fired = eng3.evaluate([rh], ctx(percent=90), 990.0)
    assert len(fired) == 1, "迟滞解除后需重新满足完整窗口"

    # ---- 静音：muted 期间照常产出但标记 muted ----
    eng4, store = make_engine()
    rk = LEAK_RULE
    eng4.mute(0.0, 300)
    fired = eng4.evaluate([rk], ctx(hhelper=16.0), 1.0)
    assert len(fired) == 1 and fired[0]["muted"] is True, fired
    eng4.muted_until = 0
    eng4.cooldown_until_by_name["leak_test"] = 0   # 越过冷却，专注验证 muted 标记
    fired = eng4.evaluate([rk], ctx(hhelper=16.0), 400.0)
    assert fired[0]["muted"] is False
    assert store.get("muted_until") is not None, "静音应持久化到 kv"

    # ---- Critical 回归：无迟滞规则触发后，条件不再成立时绝不幻影再触发 ----
    eng5, _ = make_engine()
    rk2 = LEAK_RULE
    assert len(eng5.evaluate([rk2], ctx(hhelper=16.0), 0.0)) == 1   # 触发+冷却600s
    assert eng5.evaluate([rk2], ctx(hhelper=1.0), 5.0) == []        # 进程健康：不触发
    assert eng5.evaluate([rk2], ctx(hhelper=0.0), 10.0) == []       # 进程消失：不触发
    eng5.cooldown_until_by_name["leak_test"] = 0
    assert eng5.evaluate([rk2], ctx(hhelper=1.0), 700.0) == []      # 冷却后仍健康：不触发

    # ---- 迟滞语义回归：窗口期内（未触发）跌破进入阈值应重置窗口 ----
    eng6, _ = make_engine()
    rh6 = by_name(RULES, "commit_high")[0]
    eng6.evaluate([rh6], ctx(percent=90), 0.0)
    eng6.evaluate([rh6], ctx(percent=90), 30.0)
    eng6.evaluate([rh6], ctx(percent=83), 60.0)     # 未触发阶段跌到 83 → 条件不成立 → 窗口重置
    eng6.evaluate([rh6], ctx(percent=90), 90.0)     # 重新计窗
    eng6.cooldown_until_by_name["commit_high"] = 0
    assert eng6.evaluate([rh6], ctx(percent=90), 150.0) == []       # 新窗口 60s < 90s
    eng6.cooldown_until_by_name["commit_high"] = 0
    assert len(eng6.evaluate([rh6], ctx(percent=90), 180.0)) == 1   # 满 90s 触发

    # ---- MetricCtx ----
    c = ctx(percent=66, hhelper=3.5)
    assert c.get("memory.commit_percent") == 66
    assert c.process_commit_gb("HYPHelper.exe") == 3.5
    assert c.process_commit_gb("nope.exe") is None
    print("PASS")


if __name__ == "__main__":
    main()

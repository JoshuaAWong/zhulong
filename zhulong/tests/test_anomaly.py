from zhulong.core import anomaly
import tempfile
import time
from pathlib import Path

from zhulong.core import storage


def iso(e):
    return time.strftime("%Y-%m-%dT%H:%M:%S", time.localtime(e))


def main():
    # ① 增长型泄漏：30 分钟 2→12GB → suspect
    s = {"HYPHelper.exe": [(f"2026-10-07T10:{m:02d}:00", 2.0 + m * 0.66) for m in range(0, 16)]}
    out = anomaly.detect(s)
    assert len(out) == 1 and out[0]["level"] == "suspect" and out[0]["name"] == "HYPHelper.exe", out
    assert out[0]["growth_gb"] > 4

    # ② 大但稳定（Minecraft 式）：10GB 不涨 → 不判定
    s = {"java.exe": [(f"2026-10-07T10:{m:02d}:00", 10.4) for m in range(0, 16)]}
    assert anomaly.detect(s) == [], "稳定大户被误判"

    # ③ 红线：>20GB → redline（即使不涨）
    s = {"big.exe": [(f"2026-10-07T10:{m:02d}:00", 25.0) for m in range(0, 16)]}
    out = anomaly.detect(s)
    assert len(out) == 1 and out[0]["level"] == "redline", out

    # ④ 保护名单：游戏 25GB 也不判定
    s = {"ZenlessZoneZero.exe": [(f"2026-10-07T10:{m:02d}:00", 25.0) for m in range(0, 16)]}
    assert anomaly.detect(s) == [], "保护名单被误判"

    # ⑤ 小增长不超阈值：2→5GB（<8GB floor 且 <4GB 增量）→ 不判定
    s = {"small.exe": [(f"2026-10-07T10:{m:02d}:00", 2.0 + m * 0.2) for m in range(0, 16)]}
    assert anomaly.detect(s) == [], "小增长被误判"

    # ⑥ 单点序列不判定
    assert anomaly.detect({"solo.exe": [("2026-10-07T10:00:00", 30.0)]}) == []

    now = time.time()

    # ⑦ series_from_db：混入 cpu_top_pct / io_top_mb_s 行不污染序列（key 过滤生效）
    with tempfile.TemporaryDirectory() as td:
        conn = storage.connect(Path(td) / "t.db")
        storage.init_schema(conn)
        rows = [(iso(now - 1200 + i * 600), "proctop", f"proctop{i + 1}", 2.0 + i * 6.0,
                 "Evil.exe|100|D:\\Evil.exe") for i in range(3)]
        rows += [(iso(now - 600), "proctop", "cpu_top_pct", 999.0, "Evil.exe"),
                 (iso(now - 600), "proctop", "io_top_mb_s", 888.0, "Evil.exe")]
        storage.insert_metrics(conn, rows)
        s = anomaly.series_from_db(conn)
        assert set(s) == {"Evil.exe|100"}, s   # 非 proctopN 的行被 key IN 过滤
        assert all(v < 100 for _, v in s["Evil.exe|100"]), s   # 999/888 未混入
        out = anomaly.detect(s)
        assert len(out) == 1 and out[0]["name"] == "Evil.exe" and out[0]["level"] == "suspect", out
        conn.close()

    # ⑧ 同名进程两个 pid 实例轮换进出 Top5：各自成序互不污染，不误报
    pts_a = [(iso(now - 1500 + i * 300), 6.0) for i in range(6)]     # pid 111 平稳
    pts_b = [(iso(now - 1350 + i * 300), 10.0) for i in range(6)]    # pid 222 平稳
    s = {"java.exe|111": pts_a, "java.exe|222": pts_b}
    assert anomaly.detect(s) == [], "同名多实例轮换被误判"

    # ⑨ 带 pid 的保护名单键：svchost.exe|1234 剥掉 pid 后命中 NEVER_KILL，不判异常
    s = {"svchost.exe|1234": [(iso(now - 1500 + i * 300), 25.0) for i in range(6)]}
    assert anomaly.detect(s) == [], "svchost.exe|1234 被判异常"

    # ⑩ 红线告警环比抑制（B1）：纯函数语义四态 + suspect 豁免
    RL = lambda gb: {"name": "Leak.exe", "level": "redline", "current_gb": gb,
                     "growth_gb": 0.0, "reason": "x"}
    SP = lambda gb: {"name": "Grow.exe", "level": "suspect", "current_gb": gb,
                     "growth_gb": 5.0, "reason": "y"}

    # ① 首次越线必报（无历史记录）
    out, notified = anomaly.redline_notifiable([RL(25.0)], {})
    assert [a["name"] for a in out] == ["Leak.exe"] and notified == {"Leak.exe": 25.0}, (out, notified)

    # ② 同值/微涨（<+1GB）不重报，通知值保持上次值（步长从上次通知值起算）
    out, notified = anomaly.redline_notifiable([RL(25.9)], notified)
    assert out == [] and notified == {"Leak.exe": 25.0}, (out, notified)

    # ③ 涨超 1GB 重报，通知值更新为本次值
    out, notified = anomaly.redline_notifiable([RL(26.2)], notified)
    assert [a["name"] for a in out] == ["Leak.exe"] and notified["Leak.exe"] == 26.2, (out, notified)

    # ④ 生命周期闭环：跌破红线清状态 → 回涨按首次越线再报
    hi = {"Leak.exe|1": [(iso(now - 300), 26.0), (iso(now), 26.5)]}
    assert anomaly.prune_redline_notified(hi, notified) == notified   # 仍超红线：保留
    lo = {"Leak.exe|1": [(iso(now - 300), 26.0), (iso(now), 15.0)]}
    pruned = anomaly.prune_redline_notified(lo, notified)
    assert pruned == {}, pruned   # 跌破红线：清除
    out, notified2 = anomaly.redline_notifiable([RL(21.0)], pruned)
    assert [a["name"] for a in out] == ["Leak.exe"] and notified2["Leak.exe"] == 21.0, (out, notified2)
    # 进程消失（序列中不再出现）同样清除；序列取失败（None）不剪，防误清
    assert anomaly.prune_redline_notified({}, notified) == {}
    assert anomaly.prune_redline_notified(None, notified) == notified

    # suspect 不受抑制：同名同值反复放行，且不写通知记录
    out, notified3 = anomaly.redline_notifiable([SP(12.0)], {"Grow.exe": 12.0})
    assert [a["name"] for a in out] == ["Grow.exe"] and notified3 == {"Grow.exe": 12.0}, (out, notified3)
    print("PASS")


if __name__ == "__main__":
    main()

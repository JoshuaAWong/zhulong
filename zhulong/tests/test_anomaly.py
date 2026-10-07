from zhulong.core import anomaly


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
    print("PASS")


if __name__ == "__main__":
    main()

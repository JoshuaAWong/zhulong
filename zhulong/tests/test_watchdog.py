import importlib.util
from pathlib import Path

spec = importlib.util.spec_from_file_location(
    "watchdog", Path(__file__).resolve().parent.parent.parent / "scripts" / "watchdog.py")
wd = importlib.util.module_from_spec(spec)
spec.loader.exec_module(wd)


def main():
    hb = {"pid": 123, "ts": 1000.0}
    assert wd.decide(hb, now=1050.0, intent_exists=False, stale_s=120) == "noop"   # 心跳新鲜
    assert wd.decide(hb, now=1200.0, intent_exists=False, stale_s=120) == "restart"  # 心跳过期
    assert wd.decide(hb, now=1200.0, intent_exists=True, stale_s=120) == "noop"   # 用户主动退出
    assert wd.decide(None, now=1200.0, intent_exists=False, stale_s=120) == "restart"  # 无心跳文件
    assert wd.decide({"pid": 99999, "ts": 1200.0}, now=1200.5, intent_exists=False, stale_s=120) == "noop"
    print("PASS")


if __name__ == "__main__":
    main()

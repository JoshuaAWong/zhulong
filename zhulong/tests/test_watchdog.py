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

    # _pid_alive：PID 被无关进程复用时不视为烛龙存活（防误杀），映像名必须是 pythonw.exe
    assert wd._pid_alive(1234, out='"notepad.exe","1234","Console","1","10,000 K"\r\n') is False
    assert wd._pid_alive(1234, out='"pythonw.exe","1234","Console","1","100,000 K"\r\n') is True
    # tasklist 过滤无命中时输出提示行（非 CSV）
    assert wd._pid_alive(1234, out="信息: 没有运行的任务匹配指定的标准。\r\n") is False
    assert wd._pid_alive(0) is False and wd._pid_alive(None) is False
    print("PASS")


if __name__ == "__main__":
    main()

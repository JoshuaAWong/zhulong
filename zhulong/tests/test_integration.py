"""真实集成回归锁：proctop 序列 → anomaly.detect → anomaly_leak 必须触发。
零 fake：真实 config、临时库真实 storage、真实 Engine()（不注入 kv）、registry.load_rules() 真实规则。
覆盖：增长型泄漏序列（30 分钟 2→16GB）触发；稳定大户（10GB 不涨）不触发。"""
import tempfile
import time
from pathlib import Path

from zhulong import config
from zhulong.core import registry, scheduler, storage
from zhulong.core.engine import Engine


def _series_rows(name, pairs):
    return [(ts, "proctop", "proctop1", gb, f"{name}|111|") for ts, gb in pairs]


def main():
    with tempfile.TemporaryDirectory() as td:
        now = time.time()
        iso = lambda e: time.strftime("%Y-%m-%dT%H:%M:%S", time.localtime(e))
        # 30 分钟窗口内 2→16GB 的泄漏序列
        leak_pairs = [(iso(now - 1500 + i * 120), 2.0 + i * 1.0) for i in range(15)]
        conn = storage.connect(Path(td) / "t.db")
        storage.init_schema(conn)
        storage.insert_metrics(conn, _series_rows("FakeLeak.exe", leak_pairs) +
                               [(iso(now), "memory", "commit_percent", 50.0, "")])
        ctx = scheduler.build_ctx(conn, config)
        engine = Engine()
        fired = engine.evaluate(registry.load_rules(), ctx, time.monotonic())
        assert any(f["rule"] == "anomaly_leak" for f in fired), fired
        conn.close()

    with tempfile.TemporaryDirectory() as td:
        # 稳定大户（Minecraft 式 10GB 不涨）不得触发
        stable_pairs = [(iso(now - 1500 + i * 120), 10.4) for i in range(15)]
        conn = storage.connect(Path(td) / "t.db")
        storage.init_schema(conn)
        storage.insert_metrics(conn, _series_rows("java.exe", stable_pairs) +
                               [(iso(now), "memory", "commit_percent", 50.0, "")])
        ctx = scheduler.build_ctx(conn, config)
        fired = Engine().evaluate(registry.load_rules(), ctx, time.monotonic())
        assert not any(f["rule"] == "anomaly_leak" for f in fired), fired
        conn.close()
    print("PASS")


if __name__ == "__main__":
    main()

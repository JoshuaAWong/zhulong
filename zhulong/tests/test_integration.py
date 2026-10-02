"""真实集成回归锁：build_ctx 键大小写归一化 → hyphelper_leak 必须触发。
零 fake：真实 config、临时库真实 storage、真实 Engine()（不注入 kv）、registry.load_rules() 真实规则。
F1 修复前必失败（键原始大小写时 process_commit_gb 返回 None），防复发。"""
import tempfile
import time
from pathlib import Path

from zhulong import config
from zhulong.core import registry, scheduler, storage
from zhulong.core.engine import Engine


def main():
    with tempfile.TemporaryDirectory() as td:
        conn = storage.connect(Path(td) / "t.db")
        storage.init_schema(conn)
        storage.insert_metrics(conn, [
            ("2026-10-02T10:00:00", "process", "HYPHelper_commit_gb", 16.0, "111"),
            ("2026-10-02T10:00:00", "memory", "commit_percent", 50.0, ""),
        ])
        ctx = scheduler.build_ctx(conn, config)
        engine = Engine()
        rules = registry.load_rules()
        fired = engine.evaluate(rules, ctx, time.monotonic())
        assert any(f["rule"] == "hyphelper_leak" for f in fired), fired
        conn.close()
    print("PASS")


if __name__ == "__main__":
    main()

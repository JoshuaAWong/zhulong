import sqlite3
import tempfile
from pathlib import Path

from zhulong.core import storage


def main():
    with tempfile.TemporaryDirectory() as td:
        conn = storage.connect(Path(td) / "t.db")
        storage.init_schema(conn)

        # 窄表写入与 latest
        storage.insert_metrics(conn, [
            ("2026-10-02T10:00:00", "memory", "commit_percent", 50.0, ""),
            ("2026-10-02T10:00:30", "memory", "commit_percent", 60.0, ""),
            ("2026-10-02T10:01:00", "process", "HYPHelper_commit_gb", 16.0, "pid:123"),
        ])
        row = storage.latest(conn, "memory", "commit_percent")
        assert row[1] == 60.0, row
        assert storage.latest(conn, "memory", "nope") is None

        # 历史分桶（300s 桶：10:00:00 与 10:00:30 同桶 → 均值 55）
        hist = storage.query_history(conn, "memory", "commit_percent", hours=24)
        assert len(hist) == 1 and abs(hist[0][1] - 55.0) < 0.01, hist

        # 事件
        storage.insert_event(conn, "2026-10-02T10:02:00", "hyphelper_leak", "kill_process", '{"pid":123}')
        evts = storage.query_events(conn, limit=10)
        assert evts[0][1] == "hyphelper_leak", evts

        # 清理：只留最近 keep_days —— 用 30 天前的时间戳验证
        storage.insert_metrics(conn, [("2026-08-01T10:00:00", "memory", "commit_percent", 1.0, "")])
        storage.cleanup(conn, keep_days=30)
        assert storage.latest(conn, "memory", "commit_percent")[1] == 60.0

        # kv
        storage.kv_set(conn, "muted_until", "123.45")
        assert storage.kv_get(conn, "muted_until") == "123.45"
        assert storage.kv_get(conn, "nope") is None
        conn.close()
    print("PASS")


if __name__ == "__main__":
    main()

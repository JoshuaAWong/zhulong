import tempfile
import time
from datetime import datetime
from pathlib import Path

from zhulong.core import storage


def _iso(epoch):
    return datetime.fromtimestamp(epoch).strftime("%Y-%m-%dT%H:%M:%S")


def main():
    # 相对时间生成时间戳（防测试腐烂）；base 对齐 5 分钟桶边界，避免跨桶抖动
    base = int(time.time() // 300) * 300
    with tempfile.TemporaryDirectory() as td:
        conn = storage.connect(Path(td) / "t.db")
        storage.init_schema(conn)

        # 窄表写入与 latest
        storage.insert_metrics(conn, [
            (_iso(base), "memory", "commit_percent", 50.0, ""),
            (_iso(base + 30), "memory", "commit_percent", 60.0, ""),
            (_iso(base + 60), "process", "HYPHelper_commit_gb", 16.0, "pid:123"),
        ])
        row = storage.latest(conn, "memory", "commit_percent")
        assert row[1] == 60.0, row
        assert storage.latest(conn, "memory", "nope") is None

        # 历史分桶（base 与 base+30 同落一个 5 分钟桶 → 均值 55）
        hist = storage.query_history(conn, "memory", "commit_percent", hours=24)
        assert len(hist) == 1 and abs(hist[0][1] - 55.0) < 0.01, hist

        # 事件（行形 ts/rule/action/detail）
        storage.insert_event(conn, _iso(base + 120), "hyphelper_leak", "kill_process", '{"pid":123}')
        evts = storage.query_events(conn, limit=10)
        assert evts[0][1] == "hyphelper_leak", evts

        # 清理：31 天前的行被 keep_days=30 删除
        storage.insert_metrics(conn, [(_iso(base - 31 * 86400), "memory", "commit_percent", 1.0, "")])
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

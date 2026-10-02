import tempfile
from pathlib import Path

from zhulong.core import storage
from zhulong.diag import render


def main():
    with tempfile.TemporaryDirectory() as td:
        conn = storage.connect(Path(td) / "t.db")
        storage.init_schema(conn)
        storage.insert_metrics(conn, [
            ("2026-10-02T10:00:00", "memory", "commit_percent", 88.0, ""),
            # 与上一条间隔 >300s，落入不同 5 分钟桶，否则 query_history 聚合为同桶均值
            ("2026-10-02T10:05:30", "memory", "commit_percent", 92.0, ""),
            ("2026-10-02T10:00:00", "process", "HYPHelper_commit_gb", 31.9, "pid:1"),
        ])
        storage.insert_event(conn, "2026-10-02T10:01:00", "hyphelper_leak", "killed", '{"pid":1}')
        text = render(conn, hours=24)
        assert "烛龙诊断" in text and "commit_percent" in text
        assert "31.9" in text and "hyphelper_leak" in text and "killed" in text, text
        assert "min=88.0" in text and "max=92.0" in text, text
        conn.close()
    print("PASS")


if __name__ == "__main__":
    main()

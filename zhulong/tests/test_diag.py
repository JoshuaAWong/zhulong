import tempfile
import time
from datetime import datetime
from pathlib import Path

from zhulong.core import storage
from zhulong.diag import render


def _iso(epoch):
    return datetime.fromtimestamp(epoch).strftime("%Y-%m-%dT%H:%M:%S")


def main():
    # 相对时间戳（防测试腐烂）；跨 5 分钟桶保证 min/max 真实分离
    base = int(time.time() // 300) * 300
    with tempfile.TemporaryDirectory() as td:
        conn = storage.connect(Path(td) / "t.db")
        try:
            storage.init_schema(conn)
            storage.insert_metrics(conn, [
                (_iso(base), "memory", "commit_percent", 88.0, ""),
                (_iso(base + 330), "memory", "commit_percent", 92.0, ""),
                (_iso(base), "process", "HYPHelper_commit_gb", 31.9, "pid:1"),
            ])
            storage.insert_event(conn, _iso(base + 60), "hyphelper_leak", "killed", '{"pid":1}')
            text = render(conn, hours=24)
            assert "烛龙诊断" in text and "commit_percent" in text
            assert "31.9" in text and "hyphelper_leak" in text and "killed" in text, text
            assert "min=88.0" in text and "max=92.0" in text, text
        finally:
            conn.close()
    print("PASS")


if __name__ == "__main__":
    main()

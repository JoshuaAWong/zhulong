"""SQLite 存储层：全项目唯一允许触碰 sqlite3 的模块。"""
import sqlite3
import time
from pathlib import Path

SCHEMA = """
CREATE TABLE IF NOT EXISTS metrics (
  ts TEXT NOT NULL, collector TEXT NOT NULL, key TEXT NOT NULL,
  value REAL, label TEXT DEFAULT '');
CREATE INDEX IF NOT EXISTS idx_metrics_ck_ts ON metrics(collector, key, ts);
CREATE INDEX IF NOT EXISTS idx_metrics_ts ON metrics(ts);
CREATE TABLE IF NOT EXISTS events (
  ts TEXT NOT NULL, rule TEXT NOT NULL, action TEXT NOT NULL, detail TEXT DEFAULT '');
CREATE INDEX IF NOT EXISTS idx_events_ts ON events(ts);
CREATE TABLE IF NOT EXISTS kv (key TEXT PRIMARY KEY, value TEXT);
"""


def connect(db_path: Path) -> sqlite3.Connection:
    db_path.parent.mkdir(parents=True, exist_ok=True)
    # 单连接跨线程复用（托盘回调/采集/Web 均可能触达）：WAL + busy_timeout 保证安全
    conn = sqlite3.connect(db_path, check_same_thread=False)
    conn.execute("PRAGMA journal_mode=WAL")
    conn.execute("PRAGMA busy_timeout=5000")
    return conn


def init_schema(conn: sqlite3.Connection) -> None:
    conn.executescript(SCHEMA)
    conn.commit()


def insert_metrics(conn, rows):
    conn.executemany("INSERT INTO metrics VALUES (?,?,?,?,?)", rows)
    conn.commit()


def insert_event(conn, ts, rule, action, detail=""):
    conn.execute("INSERT INTO events VALUES (?,?,?,?)", (ts, rule, action, detail))
    conn.commit()


def latest(conn, collector, key):
    cur = conn.execute(
        "SELECT ts, value, label FROM metrics WHERE collector=? AND key=? "
        "ORDER BY ts DESC LIMIT 1", (collector, key))
    return cur.fetchone()


def query_history(conn, collector, key, hours=24, bucket_s=300):
    cutoff = time.time() - hours * 3600
    cutoff_iso = time.strftime("%Y-%m-%dT%H:%M:%S", time.localtime(cutoff))
    # 5 分钟桶：epoch/300 取整（bucket_s 预留给未来调整粒度）
    cur = conn.execute(
        "SELECT datetime((CAST(strftime('%s', ts) AS INTEGER) / ?) * ?, 'unixepoch', 'localtime') AS bucket,"
        " AVG(value) FROM metrics WHERE collector=? AND key=? AND ts>=? "
        "GROUP BY bucket ORDER BY bucket",
        (bucket_s, bucket_s, collector, key, cutoff_iso))
    return cur.fetchall()


def query_events(conn, limit=50, hours=None):
    sql = "SELECT ts, rule, action, detail FROM events"
    args = []
    if hours is not None:   # 按最近 N 小时过滤（面板区间切换用）
        cutoff_iso = time.strftime("%Y-%m-%dT%H:%M:%S", time.localtime(time.time() - hours * 3600))
        sql += " WHERE ts >= ?"
        args.append(cutoff_iso)
    sql += " ORDER BY ts DESC LIMIT ?"
    args.append(limit)
    return conn.execute(sql, args).fetchall()


def cleanup(conn, keep_days=30):
    cutoff_iso = time.strftime("%Y-%m-%dT%H:%M:%S", time.localtime(time.time() - keep_days * 86400))
    conn.execute("DELETE FROM metrics WHERE ts < ?", (cutoff_iso,))
    conn.execute("DELETE FROM events WHERE ts < ?", (cutoff_iso,))
    conn.commit()
    conn.execute("PRAGMA wal_checkpoint(TRUNCATE)")


def kv_set(conn, key, value):
    conn.execute("INSERT INTO kv VALUES (?,?) ON CONFLICT(key) DO UPDATE SET value=excluded.value", (key, value))
    conn.commit()


def kv_get(conn, key):
    cur = conn.execute("SELECT value FROM kv WHERE key=?", (key,))
    row = cur.fetchone()
    return row[0] if row else None

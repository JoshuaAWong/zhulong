"""SQLite 存储层：全项目唯一允许触碰 sqlite3 的模块。
并发：采集/Web/托盘线程共用单连接（check_same_thread=False），
所有读写路径持模块级 RLock——本地库操作微秒级，粗粒度锁无害，
WAL+busy_timeout 只兜文件级竞争，连接级读写混用须靠这把锁（v1.5 终审修订）。"""
import sqlite3
import threading
import time
from pathlib import Path

_LOCK = threading.RLock()

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
    # 单连接跨线程复用（托盘回调/采集/Web 均可能触达）：WAL + busy_timeout 兜文件级，
    # 线程安全由本模块 _LOCK 保证（见各函数）
    conn = sqlite3.connect(db_path, check_same_thread=False)
    conn.execute("PRAGMA journal_mode=WAL")
    conn.execute("PRAGMA busy_timeout=5000")
    return conn


def init_schema(conn: sqlite3.Connection) -> None:
    with _LOCK:
        conn.executescript(SCHEMA)
        conn.commit()


def insert_metrics(conn, rows):
    with _LOCK:
        conn.executemany("INSERT INTO metrics VALUES (?,?,?,?,?)", rows)
        conn.commit()


def insert_event(conn, ts, rule, action, detail=""):
    with _LOCK:
        conn.execute("INSERT INTO events VALUES (?,?,?,?)", (ts, rule, action, detail))
        conn.commit()


def latest(conn, collector, key):
    with _LOCK:
        cur = conn.execute(
            "SELECT ts, value, label FROM metrics WHERE collector=? AND key=? "
            "ORDER BY ts DESC LIMIT 1", (collector, key))
        return cur.fetchone()


def query_history(conn, collector, key, hours=24, bucket_s=300):
    cutoff = time.time() - hours * 3600
    cutoff_iso = time.strftime("%Y-%m-%dT%H:%M:%S", time.localtime(cutoff))
    # 5 分钟桶：epoch/300 取整（bucket_s 预留给未来调整粒度）
    # 时区注意：ts 存的是本地时间，必须加 'utc' 修饰告诉 SQLite 按本地时间解析（否则当 UTC 解析，桶标签会快 8 小时）
    with _LOCK:
        cur = conn.execute(
            "SELECT datetime((CAST(strftime('%s', ts, 'utc') AS INTEGER) / ?) * ?, 'unixepoch', 'localtime') AS bucket,"
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
    with _LOCK:
        return conn.execute(sql, args).fetchall()


def cleanup(conn, keep_days=30):
    cutoff_iso = time.strftime("%Y-%m-%dT%H:%M:%S", time.localtime(time.time() - keep_days * 86400))
    with _LOCK:
        conn.execute("DELETE FROM metrics WHERE ts < ?", (cutoff_iso,))
        conn.execute("DELETE FROM events WHERE ts < ?", (cutoff_iso,))
        conn.commit()
        conn.execute("PRAGMA wal_checkpoint(TRUNCATE)")


def kv_set(conn, key, value):
    with _LOCK:
        conn.execute("INSERT INTO kv VALUES (?,?) ON CONFLICT(key) DO UPDATE SET value=excluded.value", (key, value))
        conn.commit()


def kv_get(conn, key):
    with _LOCK:
        cur = conn.execute("SELECT value FROM kv WHERE key=?", (key,))
        row = cur.fetchone()
        return row[0] if row else None

"""CLI 诊断：python -X utf8 -m zhulong.diag [--hours N]
输出最近 N 小时水位摘要 + 白名单进程状态 + 事件列表，供用户直接查看或 AI 分析。"""
import argparse

from zhulong import config
from zhulong.core import storage


def render(conn, hours):
    lines = [f"== 烛龙诊断 · 最近 {hours} 小时 =="]
    for c, k in [("memory", "commit_percent"), ("memory", "commit_used_gb"),
                 ("memory", "commit_limit_gb"), ("memory", "mem_percent")]:
        row = storage.latest(conn, c, k)
        if row:
            lines.append(f"{c}.{k} = {row[1]} @ {row[0]}")
    hist = storage.query_history(conn, "memory", "commit_percent", hours=hours)
    if hist:
        vals = [v for _, v in hist]
        lines.append(f"commit_percent {hours}h: min={min(vals):.1f} max={max(vals):.1f} "
                     f"avg={sum(vals) / len(vals):.1f} ({len(hist)} 桶)")
    for w in config.WATCH_PROCESSES:
        key = f"{w['name'].rsplit('.', 1)[0]}_commit_gb"
        row = storage.latest(conn, "process", key)
        if row:
            lines.append(f"{w['name']} = {row[1]}GB ({row[2]}) | 阈值 {w['max_commit_gb']}GB")
    lines.append("-- 最近事件 --")
    evts = storage.query_events(conn, limit=20)
    lines.extend(f"{ts} [{rule}] {action} {detail}" for ts, rule, action, detail in evts)
    if not evts:
        lines.append("(无)")
    return "\n".join(lines)


def main():
    ap = argparse.ArgumentParser(description="烛龙 CLI 诊断")
    ap.add_argument("--hours", type=int, default=24)
    args = ap.parse_args()
    conn = storage.connect(config.DB_PATH)
    print(render(conn, args.hours))


if __name__ == "__main__":
    main()

"""采集调度：单周期 run_cycle + 常驻 loop。心跳每周期刷新。"""
import json
import os
import time
from datetime import datetime

from zhulong.core import storage
from zhulong.core.engine import MetricCtx


def now_iso():
    return datetime.now().strftime("%Y-%m-%dT%H:%M:%S")


def write_heartbeat(state_dir):
    state_dir.mkdir(parents=True, exist_ok=True)
    (state_dir / "heartbeat.json").write_text(
        json.dumps({"pid": os.getpid(), "ts": time.time()}), encoding="utf-8")


def build_ctx(conn, cfg):
    metrics, processes = {}, {}
    for w in cfg.WATCH_PROCESSES:
        key = f"{w['name'].rsplit('.', 1)[0]}_commit_gb"
        row = storage.latest(conn, "process", key)
        if row and row[2] != "absent":
            processes[w["name"].lower()] = row[1]   # MetricCtx.process_commit_gb 按 name.lower() 查
    for c_key in (("memory", "commit_percent"), ("memory", "commit_used_gb"),
                  ("memory", "commit_limit_gb"), ("memory", "mem_percent")):
        row = storage.latest(conn, *c_key)
        if row:
            metrics[c_key] = row[1]
    return MetricCtx(metrics, processes)


def run_cycle(conn, cfg, collectors, rules, engine, actions, state, evaluate=True):
    ts = now_iso()
    rows = []
    failures = 0
    for c in collectors:
        try:
            for key, value, label in c["collect"](cfg):
                rows.append((ts, c["name"], key, float(value), label))
        except Exception:
            failures += 1   # 单采集器失败不影响本轮其他采集，但计数供托盘灰态呈现
    state["collect_failures"] = failures
    if rows:
        storage.insert_metrics(conn, rows)
    write_heartbeat(cfg.STATE_DIR)

    triggers = []
    if evaluate:
        ctx = build_ctx(conn, cfg)
        for t in engine.evaluate(rules, ctx, time.monotonic()):
            triggers.append(t)
            if t["muted"]:
                storage.insert_event(conn, ts, t["rule"], "muted", "动作已静音")
                continue
            for action_name in t["actions"]:
                act = actions.get(action_name)
                if act is None:
                    continue
                try:
                    result = act["run"](t.get("params", {}), cfg, state)
                    storage.insert_event(conn, now_iso(), t["rule"], action_name,
                                         json.dumps(result, ensure_ascii=False))
                except Exception as e:
                    storage.insert_event(conn, now_iso(), t["rule"], action_name,
                                         json.dumps({"status": "degraded:exception",
                                                     "error": str(e)}, ensure_ascii=False))
    # 每日清理：按天做一次（用 state 里的标记）
    day = time.strftime("%Y-%m-%d")
    if state.get("last_cleanup") != day:
        storage.cleanup(conn, cfg.RETENTION_DAYS)
        state["last_cleanup"] = day
    return triggers


def loop(conn, cfg, collectors, rules, engine, actions, stop_event, on_cycle=None):
    state = {}
    first = True
    while not stop_event.is_set():
        try:
            triggers = run_cycle(conn, cfg, collectors, rules, engine, actions, state,
                                 evaluate=not first)
        except Exception:
            state["collect_failures"] = state.get("collect_failures", 0) + 1
            triggers = []
        first = False   # 首个周期跳过评估（休眠唤醒防护：冷启动视为一次唤醒）
        if on_cycle is not None:
            try:
                on_cycle(triggers, state)
            except Exception:
                pass   # 回调（托盘刷新）异常不拖垮采集循环
        stop_event.wait(cfg.SAMPLE_INTERVAL)

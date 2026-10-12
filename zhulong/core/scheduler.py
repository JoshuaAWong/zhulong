"""采集调度：单周期 run_cycle + 常驻 loop。心跳每周期刷新。
分频统一在此实现（cycle % COLLECTOR["interval"]，缺省=1）——采集器内不得再私自分频。"""
import json
import logging
import os
import time
from datetime import datetime

from zhulong.core import anomaly, storage
from zhulong.core.engine import MetricCtx

LOG = logging.getLogger("zhulong")


def now_iso():
    return datetime.now().strftime("%Y-%m-%dT%H:%M:%S")


def write_heartbeat(state_dir):
    state_dir.mkdir(parents=True, exist_ok=True)
    # 临时文件 + os.replace 原子替换：看门狗永不读到半截 JSON
    tmp = state_dir / "heartbeat.json.tmp"
    tmp.write_text(json.dumps({"pid": os.getpid(), "ts": time.time()}), encoding="utf-8")
    os.replace(tmp, state_dir / "heartbeat.json")


def build_ctx(conn, cfg):
    metrics, processes = {}, {}
    anomalies, series = [], None
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
    try:
        series = anomaly.series_from_db(conn)
        anomalies = anomaly.detect(series)
    except Exception:
        anomalies, series = [], None   # 序列取失败：本轮不剪通知状态（防误清）
    ctx = MetricCtx(metrics, processes)
    ctx.anomalies = anomalies
    ctx.anomaly_series = series   # 环比抑制的生命周期清理由 run_cycle 复用
    return ctx


def _read_redline_notified(kv_get):
    """读环比抑制状态；kv 缺失/损坏一律视为无历史（fail-open 允许通知——
    通知缺失比通知风暴危险）。"""
    try:
        raw = kv_get("redline_notified") if kv_get else None
        d = json.loads(raw) if raw else {}
        return d if isinstance(d, dict) else {}
    except Exception:
        return {}


def run_cycle(conn, cfg, collectors, rules, engine, actions, state, evaluate=True):
    ts = now_iso()
    # 熔断计数走 kv 表：自动通道与面板通道共用同一份（kill_process 经 state 取用）
    state.setdefault("_kv_get", lambda k: storage.kv_get(conn, k))
    state.setdefault("_kv_set", lambda k, v: storage.kv_set(conn, k, v))
    state["cycle"] = state.get("cycle", 0) + 1
    failed_names = state.setdefault("_failed_collectors", set())   # 失败去重：连续失败只记一条
    rows = []
    failures = 0
    for c in collectors:
        if state["cycle"] % c.get("interval", 1) != 0:
            continue   # 统一分频（gpu=2、proctop=4，其余=1）
        try:
            for key, value, label in c["collect"](cfg):
                rows.append((ts, c["name"], key, float(value), label))
            failed_names.discard(c["name"])   # 恢复后重置：下次失败会重新记日志
        except Exception:
            failures += 1   # 单采集器失败不影响本轮其他采集，但计数供托盘灰态呈现
            if c["name"] not in failed_names:
                failed_names.add(c["name"])
                LOG.exception("采集器 %s 采集失败", c["name"])
    state["collect_failures"] = failures
    if rows:
        storage.insert_metrics(conn, rows)
    write_heartbeat(cfg.STATE_DIR)

    triggers = []
    if evaluate:
        ctx = build_ctx(conn, cfg)
        kv_get, kv_set = state.get("_kv_get"), state.get("_kv_set")
        # 红线告警环比抑制 · 生命周期闭环（每周期）：序列在但跌破红线的进程清除通知记录
        # （回落后再涨可再报）；序列消失（跌出 Top5）的保留记录——回榜按环比语义不重置
        notified = _read_redline_notified(kv_get)
        pruned = anomaly.prune_redline_notified(ctx.anomaly_series, notified)
        if kv_set and pruned != notified:
            kv_set("redline_notified", json.dumps(pruned))
        for t in engine.evaluate(rules, ctx, time.monotonic()):
            triggers.append(t)
            if t["muted"]:
                storage.insert_event(conn, ts, t["rule"], "muted", "动作已静音")
                continue
            if t["rule"] == "anomaly_leak" and getattr(ctx, "anomalies", None):
                # 环比抑制：事件照记（审计轨迹），仅抑制重复 toast；面板红标不受影响
                # （面板走 server.py 自己的 detect，不经过此过滤）
                notifiable, notified_new = anomaly.redline_notifiable(ctx.anomalies, pruned)
                if kv_set and notified_new != pruned:
                    kv_set("redline_notified", json.dumps(notified_new))
                if notifiable:
                    a = notifiable[0]
                    t["params"]["toast_body"] = a["reason"] + "，详情见面板大户榜与朱批记事。"
                    t["params"]["_anomaly"] = a   # 事件详情用
                else:
                    t["_notify_suppressed"] = True
            for action_name in t["actions"]:
                act = actions.get(action_name)
                if act is None:
                    continue
                if t.get("_notify_suppressed") and action_name == "toast":
                    storage.insert_event(conn, now_iso(), t["rule"], action_name,
                                         json.dumps({"status": "suppressed:redline_repeat",
                                                     "anomaly": ctx.anomalies[0]},
                                                    ensure_ascii=False))
                    continue
                try:
                    result = act["run"](t.get("params", {}), cfg, state)
                    if "_anomaly" in t.get("params", {}):
                        result = dict(result); result["anomaly"] = t["params"]["_anomaly"]
                    storage.insert_event(conn, now_iso(), t["rule"], action_name,
                                         json.dumps(result, ensure_ascii=False))
                except Exception as e:
                    storage.insert_event(conn, now_iso(), t["rule"], action_name,
                                         json.dumps({"status": "degraded:exception",
                                                     "error": str(e)}, ensure_ascii=False))
    # 限流器：分频幂等应用（自包含，失败不影响采集主循环）
    try:
        from zhulong import throttle
        throttle.apply_if_due(conn, cfg, state)
    except Exception:
        pass
    # 内存回收巡检：分频幂等（同上限流器，自包含不拖垮主循环）
    try:
        from zhulong import memtrim
        memtrim.apply_if_due(conn, cfg, state)
    except Exception:
        pass
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

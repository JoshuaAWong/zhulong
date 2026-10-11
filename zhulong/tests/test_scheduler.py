import tempfile
import time
from pathlib import Path

from zhulong.core import scheduler, storage
from zhulong.core.engine import Engine, MetricCtx


def main():
    with tempfile.TemporaryDirectory() as td:
        td = Path(td)
        conn = storage.connect(td / "t.db")
        storage.init_schema(conn)

        fake_collector = {"name": "memory", "interval": 1,
                          "collect": lambda cfg: [("commit_percent", 90.0, "")]}
        fired = []

        class FakeEngine:
            muted_until = 0
            def evaluate(self, rules, ctx, now):
                fired.append(ctx.get("memory.commit_percent"))
                return [{"rule": "r1", "actions": ["fake"], "params": {}, "muted": False}]

        actions = {"fake": {"name": "fake", "run": lambda p, c, s: {"status": "ok"}}}
        class C:
            STATE_DIR = td / "state"
            WATCH_PROCESSES = []
            RETENTION_DAYS = 30
        triggers = scheduler.run_cycle(conn, C, [fake_collector], [], FakeEngine(),
                                       actions, state={}, evaluate=True)
        assert triggers and triggers[0]["rule"] == "r1", triggers
        assert storage.latest(conn, "memory", "commit_percent")[1] == 90.0
        hb = (td / "state" / "heartbeat.json")
        assert hb.exists() and abs(hb.read_text(encoding="utf-8").count("pid")) == 1

        # 心跳原子写：遗留半截 .tmp 不影响正式心跳；写完正式文件始终完整可解析
        (td / "state" / "heartbeat.json.tmp").write_text('{"pid": 12', encoding="utf-8")   # 模拟崩溃遗留
        scheduler.write_heartbeat(td / "state")
        import json as _json
        hbj = _json.loads(hb.read_text(encoding="utf-8"))   # 若曾读到半截这里必抛
        assert hbj["pid"] > 0 and hbj["ts"] > 1e9, hbj
        assert not (td / "state" / "heartbeat.json.tmp").exists(), "os.replace 应收走临时文件"
        evts = storage.query_events(conn)
        assert evts[0][2] == "fake" and evts[0][3].count("ok") == 1, evts

        # muted：动作不执行，事件记 muted
        fired.clear()
        class MutedEngine(FakeEngine):
            def evaluate(self, rules, ctx, now):
                return [{"rule": "r1", "actions": ["fake"], "params": {}, "muted": True}]
        executed = []
        actions2 = {"fake": {"name": "fake", "run": lambda p, c, s: executed.append(1) or {"status": "ok"}}}
        triggers = scheduler.run_cycle(conn, C, [fake_collector], [], MutedEngine(),
                                       actions2, state={}, evaluate=True)
        assert triggers[0]["muted"] is True and not executed
        assert any(e[2] == "muted" for e in storage.query_events(conn, limit=5))

        # evaluate=False（唤醒首周期）：跳过评估但仍采集入库
        # 基线标记事件用当前时间：若用过期时间戳会被 run_cycle 内每日清理删除，干扰计数断言
        storage.insert_event(conn, time.strftime("%Y-%m-%dT%H:%M:%S"), "x", "mark", "")
        before = len(storage.query_events(conn, limit=100))
        scheduler.run_cycle(conn, C, [fake_collector], [], FakeEngine(), actions, state={}, evaluate=False)
        assert len(storage.query_events(conn, limit=100)) == before, "跳过评估时不应产生新事件"

        # ---- 红线告警环比抑制（B1）端到端：首报放行 → 同值重报抑制（事件照记）→ 涨超 1GB 再报 ----
        now = time.time()
        iso = lambda e: time.strftime("%Y-%m-%dT%H:%M:%S", time.localtime(e))
        rows = [(iso(now - 1200 + i * 300), "proctop", "proctop1", 25.0, "Leak.exe|111|")
                for i in range(5)]   # 30 分钟窗口内稳定 25GB（>20GB 红线）

        class AnomEngine:
            muted_until = 0
            def evaluate(self, rules, ctx, now):
                if getattr(ctx, "anomalies", None):
                    return [{"rule": "anomaly_leak", "actions": ["toast"], "params": {}, "muted": False}]
                return []

        toast_calls = []
        actions3 = {"toast": {"name": "toast",
                              "run": lambda p, c, s: toast_calls.append(p.get("toast_body")) or {"status": "notified"}}}
        storage.insert_metrics(conn, rows)
        st3 = {}
        scheduler.run_cycle(conn, C, [], [], AnomEngine(), actions3, state=st3, evaluate=True)
        assert len(toast_calls) == 1, toast_calls   # ① 首次越线必报
        import json as _json2
        notified = _json2.loads(storage.kv_get(conn, "redline_notified"))
        assert notified == {"Leak.exe": 25.0}, notified

        scheduler.run_cycle(conn, C, [], [], AnomEngine(), actions3, state=st3, evaluate=True)
        assert len(toast_calls) == 1, toast_calls   # ② 同值重报被抑制（toast 未再执行）
        evts = storage.query_events(conn, limit=10)
        assert any("suppressed:redline_repeat" in e[3] for e in evts), evts   # 事件照记（审计轨迹）

        storage.insert_metrics(conn, [(iso(time.time()), "proctop", "proctop1", 27.0, "Leak.exe|111|")])
        scheduler.run_cycle(conn, C, [], [], AnomEngine(), actions3, state=st3, evaluate=True)
        assert len(toast_calls) == 2, toast_calls   # ③ 涨超 1GB 再报
        notified = _json2.loads(storage.kv_get(conn, "redline_notified"))
        assert notified == {"Leak.exe": 27.0}, notified
        conn.close()
    print("PASS")


if __name__ == "__main__":
    main()

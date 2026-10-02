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
        conn.close()
    print("PASS")


if __name__ == "__main__":
    main()

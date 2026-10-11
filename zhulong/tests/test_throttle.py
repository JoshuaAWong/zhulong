import json
import tempfile
from pathlib import Path

from zhulong import throttle
from zhulong.core import storage

NORMAL = 32
BELOW = 16384


class FakeProc:
    def __init__(self, pid, name):
        self._pid, self._name = pid, name
        self._nice, self._aff = NORMAL, list(range(16))
        self.info = {"pid": pid, "name": name}

    def name(self):
        return self._name

    def nice(self, v=None):
        if v is None:
            return self._nice
        self._nice = v

    def cpu_affinity(self, v=None):
        if v is None:
            return self._aff
        self._aff = sorted(v)


class FakePS:
    NORMAL_PRIORITY_CLASS = NORMAL
    BELOW_NORMAL_PRIORITY_CLASS = BELOW
    NoSuchProcess = type("NoSuchProcess", (Exception,), {})
    AccessDenied = type("AccessDenied", (Exception,), {})

    def __init__(self, procs):
        self._procs = procs

    def cpu_count(self):
        return 16

    def process_iter(self, attrs=None):
        return list(self._procs)


class FakeCfg:
    THROTTLE_INTERVAL_CYCLES = 1
    THROTTLE_DEFAULTS = {"enabled": True, "rules": [
        {"name": "ToDesk.exe", "priority": "below_normal", "cores": "tail:2"},
        {"name": "ghost.exe", "priority": "below_normal", "cores": "tail:2"},   # 不存在 → 应跳过
    ]}
    # DATA_DIR 在 main() 里挂到临时目录（随 TemporaryDirectory 自动清理）


def main():
    with tempfile.TemporaryDirectory() as td:
        FakeCfg.DATA_DIR = Path(td) / "cfg"
        FakeCfg.DATA_DIR.mkdir()
        conn = storage.connect(Path(td) / "t.db")
        storage.init_schema(conn)
        p1, p2 = FakeProc(101, "ToDesk.exe"), FakeProc(102, "notepad.exe")
        pm = FakePS([p1, p2])
        state = {}

        # kv 未设置时按出厂 enabled 施
        throttle.apply_if_due(conn, FakeCfg, state, psutil_mod=pm)
        assert p1._nice == BELOW and p1._aff == [14, 15], (p1._nice, p1._aff)
        assert p2._nice == NORMAL and p2._aff == list(range(16)), "未命中进程不得被碰"
        applied = json.loads(storage.kv_get(conn, "throttle_applied"))
        assert "ToDesk.exe" in applied

        # 幂等：再应用不重复记事件
        ev0 = len(storage.query_events(conn, limit=100))
        throttle.apply_if_due(conn, FakeCfg, state, psutil_mod=pm)
        assert len(storage.query_events(conn, limit=100)) == ev0, "幂等失效"

        # restore 按清单还原（改规则后也不漏网）
        n = throttle.restore(conn, psutil_mod=pm)
        assert n == 1 and p1._nice == NORMAL and p1._aff == list(range(16))
        assert storage.kv_get(conn, "throttle_applied") == "{}"

        # 开关关闭不施加
        storage.kv_set(conn, "throttle_enabled", "0")
        p1._nice = NORMAL
        throttle.apply_if_due(conn, FakeCfg, state, psutil_mod=pm)
        assert p1._nice == NORMAL

        # ---- B4：rules() 每轮限流只取一次（此前在 process_iter 循环体内，200 进程读 200 次）----
        storage.kv_set(conn, "throttle_enabled", "1")
        calls = []
        orig_rules = throttle.rules
        throttle.rules = lambda cfg, override_path=None: calls.append(1) or orig_rules(cfg, override_path)
        try:
            many = [FakeProc(200 + i, f"p{i}.exe") for i in range(50)] + [FakeProc(999, "ToDesk.exe")]
            throttle.apply_if_due(conn, FakeCfg, {}, psutil_mod=FakePS(many))
            assert len(calls) == 1, calls
        finally:
            throttle.rules = orig_rules

        # ---- B4：坏规则（priority 非法 / cores 格式错）跳过且记一次事件，好规则照常施加 ----
        ovdir = Path(td) / "ov"
        ovdir.mkdir()
        (ovdir / "throttle_override.json").write_text(json.dumps({"rules": [
            {"name": "bad1.exe", "priority": "high"},                 # priority 非法
            {"name": "bad2.exe", "cores": "tail:abc"},                # cores 格式错
            {"name": "OkProc.exe", "priority": "below_normal", "cores": "tail:2"},
        ]}, ensure_ascii=False), encoding="utf-8")

        class OvrCfg:
            DATA_DIR = ovdir
            THROTTLE_INTERVAL_CYCLES = 1
            THROTTLE_DEFAULTS = FakeCfg.THROTTLE_DEFAULTS
        pok = FakeProc(300, "OkProc.exe")
        st = {}
        throttle.apply_if_due(conn, OvrCfg, st, psutil_mod=FakePS([pok]))
        assert pok._nice == BELOW and pok._aff == [14, 15], (pok._nice, pok._aff)   # 好规则生效
        skips = [e for e in storage.query_events(conn, limit=100)
                 if e[2] == "skip" and "invalid_rule" in e[3]]
        assert len(skips) == 1 and "bad1.exe" in skips[0][3] and "bad2.exe" in skips[0][3], skips
        throttle.apply_if_due(conn, OvrCfg, st, psutil_mod=FakePS([pok]))
        skips = [e for e in storage.query_events(conn, limit=100)
                 if e[2] == "skip" and "invalid_rule" in e[3]]
        assert len(skips) == 1, skips   # 坏规则内容不变不重复记（防每 2 分钟刷屏）

        # ---- B4：AccessDenied（自保护进程）记一次 throttle/skip 事件，不再静默 ----
        class DenyProc(FakeProc):
            def nice(self, v=None):
                if v is None:
                    return self._nice
                raise FakePS.AccessDenied("denied")
        pd = DenyProc(400, "ToDesk.exe")
        throttle.apply_if_due(conn, FakeCfg, {}, psutil_mod=FakePS([pd]))
        skips = [e for e in storage.query_events(conn, limit=100)
                 if e[2] == "skip" and "access_denied" in e[3]]
        assert len(skips) == 1 and "ToDesk.exe" in skips[0][3], skips

        # 终审修订：denied 集合不变不重复记（自保护进程永远 AccessDenied，每轮记会刷屏）
        def deny_event_count():
            return len([e for e in storage.query_events(conn, limit=100)
                        if e[2] == "skip" and "access_denied" in e[3]])
        st6 = {}
        throttle.apply_if_due(conn, FakeCfg, st6, psutil_mod=FakePS([DenyProc(500, "ToDesk.exe")]))
        n_ev = deny_event_count()
        throttle.apply_if_due(conn, FakeCfg, st6, psutil_mod=FakePS([DenyProc(501, "ToDesk.exe")]))
        assert deny_event_count() == n_ev, "同一 denied 集合重复记事件"
        # 集合变化（新增 ghost.exe 也被拒）→ 再记一条
        throttle.apply_if_due(conn, FakeCfg, st6,
                              psutil_mod=FakePS([DenyProc(502, "ToDesk.exe"),
                                                 DenyProc(503, "ghost.exe")]))
        assert deny_event_count() == n_ev + 1, "denied 集合变化未记事件"
        # 集合收窄（恢复无阻）→ 不记；再次被拒（集合重新非空）→ 记
        throttle.apply_if_due(conn, FakeCfg, st6, psutil_mod=FakePS([]))
        assert deny_event_count() == n_ev + 1
        throttle.apply_if_due(conn, FakeCfg, st6, psutil_mod=FakePS([DenyProc(504, "ToDesk.exe")]))
        assert deny_event_count() == n_ev + 2, "denied 集合清空后再 denied 应重新记"

        # ---- B4：restore 幂等（do_exit 退出路径统一调 restore 的前提）----
        n1 = throttle.restore(conn, psutil_mod=FakePS([pok]))
        assert n1 == 1 and pok._nice == NORMAL and pok._aff == list(range(16)), n1
        n2 = throttle.restore(conn, psutil_mod=FakePS([pok]))
        assert n2 == 0, n2   # 清单已空，再调无副作用

        # ---- C2 红线负例：限流器永不处理自身进程（apply/restore 均跳过 os.getpid()）----
        import os
        self_proc = FakeProc(os.getpid(), "ToDesk.exe")   # 名字命中规则但 PID 是自身
        throttle.apply_if_due(conn, FakeCfg, {}, psutil_mod=FakePS([self_proc]))
        assert self_proc._nice == NORMAL and self_proc._aff == list(range(16)), "自身进程被限流"
        storage.kv_set(conn, "throttle_applied",
                       json.dumps({"ToDesk.exe": {"priority": "below_normal", "cores": [14, 15]}}))
        n = throttle.restore(conn, psutil_mod=FakePS([self_proc]))
        assert n == 0 and self_proc._nice == NORMAL, "restore 碰了自身进程"
        conn.close()
    print("PASS")


if __name__ == "__main__":
    main()

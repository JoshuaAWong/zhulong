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
    DATA_DIR = Path(tempfile.mkdtemp())
    THROTTLE_INTERVAL_CYCLES = 1
    THROTTLE_DEFAULTS = {"enabled": True, "rules": [
        {"name": "ToDesk.exe", "priority": "below_normal", "cores": "tail:2"},
        {"name": "ghost.exe", "priority": "below_normal", "cores": "tail:2"},   # 不存在 → 应跳过
    ]}


def main():
    with tempfile.TemporaryDirectory() as td:
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
        conn.close()
    print("PASS")


if __name__ == "__main__":
    main()

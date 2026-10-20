"""v1.7 进程池泄洪：watch_key / 采集分桶 / 聚合触发 / oldest 受害者顺延 /
同名多规则熔断独立 / pool_high 规则条件。v1.3 行为回归由 test_actions.py 覆盖。"""
import time

from zhulong import config
from zhulong.actions import kill_process
from zhulong.collectors import process
from zhulong.core.engine import MetricCtx
from zhulong import rules as rules_mod

GB = 1024 ** 3
NOW = time.time()

EXE_CODEGRAPH = r"C:\Users\x\AppData\Roaming\npm\node_modules\@colbymchenry\codegraph\bin\node.exe"
EXE_CUA = r"C:\Users\x\AppData\Local\OpenAI\Codex\runtimes\cua_node\abc\bin\node.exe"
EXE_PLAIN = r"C:\Program Files\nodejs\node.exe"


class FakeProc:
    def __init__(self, pid, name, commit_bytes, create_time, exe):
        self.info = {"pid": pid, "name": name,
                     "memory_info": type("MI", (), {"commit": commit_bytes, "vms": commit_bytes})(),
                     "create_time": create_time, "exe": exe}
        self._exe = exe
        self.killed = False

    def exe(self):
        return self._exe

    def create_time(self):
        return self.info["create_time"]

    def kill(self):
        self.killed = True


class FakePS:
    AccessDenied = type("AccessDenied", (Exception,), {})
    NoSuchProcess = type("NoSuchProcess", (Exception,), {})

    def __init__(self, procs):
        self._procs = procs

    def process_iter(self, attrs=None):
        return list(self._procs)


NODE_RULES = [
    {"name": "node.exe", "display_name": "codegraph MCP 池",
     "group_commit_gb": 3, "victim_by": "oldest", "min_age_s": 3600,
     "max_kills_per_10min": 2, "path_contains": "@colbymchenry"},
    {"name": "node.exe", "display_name": "Codex runtime 池",
     "group_commit_gb": 4, "victim_by": "oldest", "min_age_s": 3600,
     "max_kills_per_10min": 2, "path_contains": "cua_node"},
]


class C:
    # 带 path 无 group 的 HYPHelper 形态一并纳入，验证旧 key 在分桶采集下不变
    WATCH_PROCESSES = NODE_RULES + [
        {"name": "HYPHelper.exe", "display_name": "米哈游启动器组件",
         "max_commit_gb": 15, "min_age_s": 60, "max_kills_per_10min": 2,
         "path_contains": "miHoYo Launcher"},
    ]


def main():
    # ---- watch_key：旧 key 兼容 + 池规则 slug ----
    hyp = next(w for w in config.WATCH_PROCESSES if w["name"] == "HYPHelper.exe")
    assert config.watch_key(hyp) == "HYPHelper", config.watch_key(hyp)   # 带 path 也保持旧 key
    assert config.watch_key(NODE_RULES[0]) == "node_colbymchenry"
    assert config.watch_key(NODE_RULES[1]) == "node_cua_node"

    # ---- 采集分桶：同名多规则互不覆盖，按 path 分组 ----
    fake_iter = lambda: [
        FakeProc(1, "node.exe", 1.0 * GB, NOW - 7200, EXE_CODEGRAPH),
        FakeProc(2, "node.exe", 2.5 * GB, NOW - 86400, EXE_CODEGRAPH),
        FakeProc(3, "node.exe", 4.5 * GB, NOW - 3600, EXE_CUA),
        FakeProc(4, "node.exe", 9.0 * GB, NOW - 60, EXE_PLAIN),   # 不属于任何池
        FakeProc(5, "node.exe", 0.1 * GB, NOW - 60, None),        # exe 读不到：不进组
        FakeProc(6, "HYPHelper.exe", 16 * GB, NOW - 3600,
                 r"D:\Game\miHoYo Launcher\HYPHelper.exe"),
    ]
    rows = process.collect(C(), process_iter=fake_iter)
    d = {k: (v, l) for k, v, l in rows}
    assert abs(d["node_colbymchenry_commit_gb"][0] - 3.5) < 0.01, d   # 1.0+2.5 聚合
    assert d["node_colbymchenry_commit_gb"][1] == "2个", d
    assert abs(d["node_cua_node_commit_gb"][0] - 4.5) < 0.01, d
    assert d["HYPHelper_commit_gb"][1] == "6", d   # 单进程口径：label=pid，key 不变
    assert not any(k.startswith("node_commit_gb") for k in d), d      # 无 group 的 node 不落裸 key

    # ---- kill：聚合超阈值触发，victim=oldest 杀最老 ----
    old, mid = NOW - 86400, NOW - 7200
    young = NOW - 60
    cg_old = FakeProc(11, "node.exe", 1.2 * GB, old, EXE_CODEGRAPH)
    cg_mid = FakeProc(12, "node.exe", 2.0 * GB, mid, EXE_CODEGRAPH)
    plain = FakeProc(13, "node.exe", 0.5 * GB, old, EXE_PLAIN)
    r = kill_process.run("node.exe", C(), {}, psutil_mod=FakePS([cg_old, cg_mid, plain]))
    assert r["status"] == "killed" and r["pid"] == 11 and cg_old.killed, r   # 3.2GB≥3，最老
    assert not cg_mid.killed and not plain.killed, "只杀组内一个，池外不误伤"

    # ---- oldest 顺延：最老不足龄 → 次老 ----
    cg_young_old = FakeProc(21, "node.exe", 2.9 * GB, young, EXE_CODEGRAPH)
    cg_old2 = FakeProc(22, "node.exe", 0.2 * GB, old, EXE_CODEGRAPH)
    r = kill_process.run("node.exe", C(), {}, psutil_mod=FakePS([cg_young_old, cg_old2]))
    assert r["status"] == "killed" and r["pid"] == 22 and cg_old2.killed, r   # 顺延次老

    # ---- 全组不足龄 → all_too_young ----
    cg_y1 = FakeProc(31, "node.exe", 2.9 * GB, young, EXE_CODEGRAPH)
    cg_y2 = FakeProc(32, "node.exe", 0.2 * GB, NOW - 1800, EXE_CODEGRAPH)
    r = kill_process.run("node.exe", C(), {}, psutil_mod=FakePS([cg_y1, cg_y2]))
    assert r["status"] == "skipped:all_too_young" and not cg_y1.killed, r

    # ---- 聚合未超阈值（且单进程口径未配）→ 不处置；同名下一条规则继续判定 ----
    cua_big = FakeProc(41, "node.exe", 4.2 * GB, old, EXE_CUA)
    cg_small = FakeProc(42, "node.exe", 1.0 * GB, old, EXE_CODEGRAPH)   # codegraph 池 1GB<3 不触发
    r = kill_process.run("node.exe", C(), {}, psutil_mod=FakePS([cg_small, cua_big]))
    assert r["status"] == "killed" and r["pid"] == 41 and cua_big.killed, r   # 落到 cua 组
    assert not cg_small.killed, r

    # ---- 熔断独立：codegraph 组满限不影响 cua 组（同名不同桶） ----
    kv = {}
    kvg, kvs = (lambda k: kv.get(k)), (lambda k, v: kv.__setitem__(k, v))
    state = {}
    cg_a = FakeProc(51, "node.exe", 2.9 * GB, old, EXE_CODEGRAPH)
    cg_b = FakeProc(52, "node.exe", 0.2 * GB, old, EXE_CODEGRAPH)
    r = kill_process.run("node.exe", C(), state, psutil_mod=FakePS([cg_a, cg_b]),
                         kv_get=kvg, kv_set=kvs)
    assert r["status"] == "killed" and r["pid"] == 51, r
    r = kill_process.run("node.exe", C(), state, psutil_mod=FakePS([cg_b]))   # 组内水位 0.2<3，本就不触发
    assert r["status"].startswith("skipped"), r
    import json as _json
    times = _json.loads(kv["kill_times"])["node_colbymchenry"]
    assert len(times) == 1, kv
    # cua 桶独立：codegraph 桶已有 1 次，cua 组触发不受影响
    cua_x = FakeProc(53, "node.exe", 4.1 * GB, old, EXE_CUA)
    r = kill_process.run("node.exe", C(), state, psutil_mod=FakePS([cua_x]),
                         kv_get=kvg, kv_set=kvs)
    assert r["status"] == "killed" and r["pid"] == 53 and cua_x.killed, r
    assert _json.loads(kv["kill_times"])["node_cua_node"], kv   # 各自记账

    # ---- pool_high 规则条件：双保险（组聚合 × 系统水位） ----
    pool_rules = [x for x in rules_mod.RULES if x["name"].startswith("pool_high_")]
    assert len(pool_rules) == 2, [x["name"] for x in pool_rules]   # 真实 config 的两条 node 池
    cond = next(x for x in pool_rules if "colbymchenry" in x["name"])["condition"]
    hi = MetricCtx({("pool", "node_colbymchenry_gb"): 3.5,
                    ("memory", "commit_percent"): 90.0}, {})
    lo_mem = MetricCtx({("pool", "node_colbymchenry_gb"): 3.5,
                        ("memory", "commit_percent"): 84.9}, {})
    lo_pool = MetricCtx({("pool", "node_colbymchenry_gb"): 2.9,
                         ("memory", "commit_percent"): 95.0}, {})
    missing = MetricCtx({("memory", "commit_percent"): 95.0}, {})   # 采集缺数据
    assert cond(hi) is True or cond(hi) == True, "聚合超+高水位 → 触发"
    assert not cond(lo_mem), "系统不紧张（<85%）不折腾"
    assert not cond(lo_pool), "池未超不触发"
    assert not cond(missing), "缺池数据不触发"
    # 动态规则参数指向正确进程名
    for x in pool_rules:
        assert x["params"]["process"] == "node.exe", x["params"]

    print("PASS")


if __name__ == "__main__":
    main()

"""规则引擎：唯一持有运行态（窗口/冷却/迟滞/静音）的模块。
时间纪律：窗口与冷却一律用 monotonic；静音（muted_until）用墙钟并落库（重启后仍生效）。"""
import time


class MetricCtx:
    def __init__(self, metrics, processes):
        self.metrics = metrics            # {(collector,key): value}
        self.processes = processes        # {name_lower: commit_gb}

    def get(self, dotted):
        c, k = dotted.split(".", 1)
        return self.metrics.get((c, k))

    def process_commit_gb(self, name):
        return self.processes.get(name.lower())


class Engine:
    def __init__(self, kv_get=None, kv_set=None):
        self.since = {}                   # name -> monotonic 窗口起点
        self.cooldown_until_by_name = {}  # name -> monotonic
        self.alarm_on = {}                # name -> bool（仅迟滞规则的"已触发"锁存态）
        self.cycle = 0
        self._kv_get = kv_get or (lambda k: None)
        self._kv_set = kv_set or (lambda k, v: None)
        # muted_until 落库用墙钟（monotonic 重启即失效）。<1e9 的值是旧版本写入的
        # monotonic 残留，丢弃视为无静音——否则会被当成 1970 年的时间戳永久静音
        v = float(self._kv_get("muted_until") or 0)
        self.muted_until = v if v >= 1e9 else 0.0

    def mute(self, now_mono, seconds):
        # now_mono 保留仅为签名兼容；落库与比较一律墙钟（重启后静音仍生效）
        self.muted_until = max(self.muted_until, time.time() + seconds)
        self._kv_set("muted_until", repr(self.muted_until))

    def evaluate(self, rules, ctx, now_mono):
        self.cycle += 1
        fired = []
        for rule in rules:
            if self.cycle % rule.get("every", 1) != 0:
                continue
            name = rule["name"]
            if self.alarm_on.get(name, False) and rule.get("reset_below"):
                # 已触发态（仅迟滞规则有锁存）：跌破退出阈值才解除，解除即清窗口；
                # 未跌破则视为条件持续成立（80<x<85 仍算红）
                if rule["reset_below"](ctx):
                    self.alarm_on[name] = False
                    self.since[name] = None
                    continue
                cond = True
            else:
                cond = rule["condition"](ctx)
            if not cond:
                self.since[name] = None
                continue
            start = self.since.get(name)
            if start is None:
                self.since[name] = start = now_mono
            if now_mono - start < rule.get("for_seconds", 0):
                continue
            if now_mono < self.cooldown_until_by_name.get(name, 0):
                continue
            muted = time.time() < self.muted_until   # 与落库口径一致：墙钟比较
            if not muted:
                self.cooldown_until_by_name[name] = now_mono + rule.get("cooldown_seconds", 0)
            if rule.get("reset_below"):
                self.alarm_on[name] = True   # 触发即进入锁存态（仅迟滞规则）
            self.since[name] = None
            fired.append({"rule": name, "actions": rule["actions"],
                          # params 浅拷贝递出：scheduler 会往里写 toast_body/_anomaly，
                          # 按引用递出会永久污染 rules.py 模块级共享 dict（默认文案丢失）
                          "params": dict(rule.get("params", {})),
                          "muted": muted})
        return fired

# 语义要点（终审与后续维护必读）：
# - 锁存仅属于迟滞规则（有 reset_below 的 commit_high）：触发后才进入"已触发"态，
#   期间 80<x<85 视为持续成立，跌破 ≤80 解除并清窗口；解除后需重新计满完整窗口才再触发
# - 无迟滞规则（hyphelper_leak）每次评估都走真实条件——进程健康/消失后绝不幻影再触发
# - 窗口期内（未触发）跌破进入阈值（如 90→83）视为条件不成立，窗口重置

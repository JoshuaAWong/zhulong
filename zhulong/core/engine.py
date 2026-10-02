"""规则引擎：唯一持有运行态（窗口/冷却/迟滞/静音）的模块。
时间纪律：窗口与冷却一律用 monotonic；落库时间由调用方用 wall clock 生成。"""
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
        self.alarm_on = {}                # name -> bool（迟滞态）
        self.cycle = 0
        self._kv_get = kv_get or (lambda k: None)
        self._kv_set = kv_set or (lambda k, v: None)
        self.muted_until = float(self._kv_get("muted_until") or 0)

    def mute(self, now_mono, seconds):
        self.muted_until = max(self.muted_until, now_mono + seconds)
        self._kv_set("muted_until", repr(self.muted_until))

    def evaluate(self, rules, ctx, now_mono):
        self.cycle += 1
        fired = []
        for rule in rules:
            if self.cycle % rule.get("every", 1) != 0:
                continue
            name = rule["name"]
            on = self.alarm_on.get(name, False)
            # 迟滞：触发态下，值 ≤ reset_below 才解除；否则条件视为持续成立
            if on:
                cond = not (rule.get("reset_below") and rule["reset_below"](ctx))
            else:
                cond = rule["condition"](ctx)
                if cond:
                    self.alarm_on[name] = True
            if not cond:
                self.since[name] = None
                self.alarm_on[name] = False
                continue
            start = self.since.get(name)
            if start is None:             # None 表示窗口未开始或已被清空
                start = self.since[name] = now_mono
            if now_mono - start < rule.get("for_seconds", 0):
                continue
            if now_mono < self.cooldown_until_by_name.get(name, 0):
                continue
            self.cooldown_until_by_name[name] = now_mono + rule.get("cooldown_seconds", 0)
            self.since[name] = None
            fired.append({"rule": name, "actions": rule["actions"],
                          "params": rule.get("params", {}),
                          "muted": now_mono < self.muted_until})
        return fired

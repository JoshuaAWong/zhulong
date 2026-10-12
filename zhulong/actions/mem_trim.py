"""面板/规则通道的内存整理动作：薄转发到 memtrim.trim_once（同一条安全链与熔断）。"""
from zhulong import memtrim

ACTION = {"name": "mem_trim",
          "run": lambda params, cfg, state: memtrim.trim_once(
              params.get("process", ""), cfg, state,
              kv_get=state.get("_kv_get") if state else None,
              kv_set=state.get("_kv_set") if state else None)}

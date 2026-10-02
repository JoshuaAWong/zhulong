"""扩展点自动发现：collectors/ 与 actions/ 目录扫描 + rules.py 加载。
约定：collector/action 模块级暴露 COLLECTOR / ACTION dict；规则集中在 rules.py 的 RULES 列表。
扔文件进目录即生效，无需手工注册。"""
import importlib
import pkgutil


def discover(pkg) -> list:
    found = []
    for m in pkgutil.iter_modules(pkg.__path__):
        mod = importlib.import_module(f"{pkg.__name__}.{m.name}")
        item = getattr(mod, "COLLECTOR", None) or getattr(mod, "ACTION", None)
        if item:
            found.append(item)
    return found


def load_rules() -> list:
    from zhulong import rules as rules_mod
    return list(rules_mod.RULES)

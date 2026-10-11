"""扩展点自动发现：collectors/ 与 actions/ 目录扫描 + rules.py 加载。
约定：collector/action 模块级暴露 COLLECTOR / ACTION dict；规则集中在 rules.py 的 RULES 列表。
扔文件进目录即生效，无需手工注册；坏文件（语法/导入错误）记日志跳过，不拖死整机。"""
import importlib
import logging
import pkgutil

LOG = logging.getLogger("zhulong")


def discover(pkg) -> list:
    found = []
    for m in pkgutil.iter_modules(pkg.__path__):
        try:
            mod = importlib.import_module(f"{pkg.__name__}.{m.name}")
        except Exception:
            LOG.exception("扩展点 %s.%s 加载失败，跳过", pkg.__name__, m.name)
            continue
        item = getattr(mod, "COLLECTOR", None) or getattr(mod, "ACTION", None)
        if item:
            if any(x.get("name") == item.get("name") for x in found):
                LOG.warning("扩展点同名冲突：%s（%s；actions dict 同名后者覆盖前者）",
                            item.get("name"), m.name)
            found.append(item)
    return found


def load_rules() -> list:
    from zhulong import rules as rules_mod
    return list(rules_mod.RULES)

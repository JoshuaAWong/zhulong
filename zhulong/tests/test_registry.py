import tempfile
from pathlib import Path


def main():
    # 构造一个临时包：一个含 COLLECTOR 的模块、一个不含的、一个语法错误的、一个同名冲突的
    with tempfile.TemporaryDirectory() as td:
        pkg = Path(td) / "fakecollectors"
        pkg.mkdir()
        (pkg / "__init__.py").write_text("", encoding="utf-8")
        (pkg / "good.py").write_text(
            "COLLECTOR = {'name':'fake','interval':1,'collect': lambda cfg: []}\n",
            encoding="utf-8")
        (pkg / "plain.py").write_text("X = 1\n", encoding="utf-8")
        (pkg / "bad.py").write_text("def broken(:\n", encoding="utf-8")   # 语法错误
        (pkg / "dup.py").write_text(
            "COLLECTOR = {'name':'fake','interval':1,'collect': lambda cfg: []}\n",
            encoding="utf-8")   # 与 good.py 同名冲突

        import importlib.util
        import sys
        spec = importlib.util.spec_from_file_location("fakecollectors", pkg / "__init__.py")
        mod = importlib.util.module_from_spec(spec)
        sys.modules["fakecollectors"] = mod
        spec.loader.exec_module(mod)

        import logging
        from zhulong.core import registry
        recs = []
        h = logging.Handler()
        h.emit = lambda r: recs.append(r.getMessage())
        logging.getLogger("zhulong").addHandler(h)
        try:
            found = registry.discover(mod)
        finally:
            logging.getLogger("zhulong").removeHandler(h)
        # 坏文件记日志跳过、其余正常加载；同名冲突记 warning 但都保留（actions dict 后者覆盖）
        assert [x["name"] for x in found] == ["fake", "fake"], found
        assert any("加载失败" in m for m in recs), recs
        assert any("同名冲突" in m for m in recs), recs

        # load_rules 返回 list（首版仅校验类型；规则内容在 Task 5 填充后再测行为）
        rules = registry.load_rules()
        assert isinstance(rules, list), rules
    print("PASS")


if __name__ == "__main__":
    main()

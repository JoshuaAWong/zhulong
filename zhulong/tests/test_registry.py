import tempfile
from pathlib import Path


def main():
    # 构造一个临时包：一个含 COLLECTOR 的模块、一个不含的
    with tempfile.TemporaryDirectory() as td:
        pkg = Path(td) / "fakecollectors"
        pkg.mkdir()
        (pkg / "__init__.py").write_text("", encoding="utf-8")
        (pkg / "good.py").write_text(
            "COLLECTOR = {'name':'fake','interval':1,'collect': lambda cfg: []}\n",
            encoding="utf-8")
        (pkg / "plain.py").write_text("X = 1\n", encoding="utf-8")

        import importlib.util
        import sys
        spec = importlib.util.spec_from_file_location("fakecollectors", pkg / "__init__.py")
        mod = importlib.util.module_from_spec(spec)
        sys.modules["fakecollectors"] = mod
        spec.loader.exec_module(mod)

        from zhulong.core import registry
        found = registry.discover(mod)
        assert len(found) == 1 and found[0]["name"] == "fake", found

        # load_rules 返回 list（首版仅校验类型；规则内容在 Task 5 填充后再测行为）
        rules = registry.load_rules()
        assert isinstance(rules, list), rules
    print("PASS")


if __name__ == "__main__":
    main()

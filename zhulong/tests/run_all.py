"""总跑入口：python -X utf8 -m zhulong.tests.run_all
逐个 subprocess 跑 test_*.py（隔离模块级状态），汇总 PASS/FAIL，有失败则非零退出。"""
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent.parent


def main():
    tests = sorted(Path(__file__).parent.glob("test_*.py"))
    failed = []
    for t in tests:
        name = t.stem
        r = subprocess.run([sys.executable, "-X", "utf8", "-m", f"zhulong.tests.{name}"],
                           cwd=ROOT, capture_output=True, text=True, encoding="utf-8")
        ok = r.returncode == 0 and "PASS" in (r.stdout or "")
        print(f"{name:24s} {'PASS' if ok else 'FAIL'}")
        if not ok:
            failed.append(name)
            print((r.stdout or "") + (r.stderr or ""))   # 失败时带出输出便于定位
    print(f"\n{len(tests) - len(failed)}/{len(tests)} PASS")
    if failed:
        print("FAIL:", ", ".join(failed))
        sys.exit(1)


if __name__ == "__main__":
    main()

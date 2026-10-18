"""解释器闭环测试：当前实例复用 + 安装发现脚本 + XML 生成转义。"""
import os
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET
from pathlib import Path

from zhulong.config import runtime_pythonw

ROOT = Path(__file__).resolve().parent.parent.parent
FIND = ROOT / "scripts" / "find_python.ps1"
PREPARE = ROOT / "scripts" / "prepare_tasks.ps1"


def run_ps(script, env):
    return subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(script)],
                          env=env, capture_output=True, text=True, encoding="utf-8", errors="replace")


def pairs(output):
    return dict(line.split("=", 1) for line in output.strip().splitlines() if "=" in line)


def main():
    with tempfile.TemporaryDirectory() as td:
        td = Path(td)
        python = td / "python.exe"
        pythonw = td / "pythonw.exe"
        python.write_text("", encoding="utf-8")
        pythonw.write_text("", encoding="utf-8")
        # python.exe 调试时选同目录 pythonw；生产环境 pythonw 直接复用当前实例
        assert runtime_pythonw(str(python)) == pythonw
        assert runtime_pythonw(str(pythonw)) == pythonw
        # 同目录无 pythonw 时不跨安装目录猜测
        other = td / "other" / "python.exe"
        other.parent.mkdir()
        other.write_text("", encoding="utf-8")
        assert runtime_pythonw(str(other)) == other

        env = os.environ.copy()
        env["ZHULONG_PYTHON"] = sys.executable
        env["ZHULONG_PYTHONW"] = str(runtime_pythonw())
        result = run_ps(FIND, env)
        assert result.returncode == 0, result.stderr
        found = pairs(result.stdout)
        assert Path(found["PY"]).resolve() == Path(sys.executable).resolve()
        assert Path(found["PYW"]).resolve() == runtime_pythonw().resolve()

        # 单边覆盖：只给 python.exe，pythonw 从同目录补全
        single = env.copy()
        single.pop("ZHULONG_PYTHONW", None)
        result = run_ps(FIND, single)
        assert result.returncode == 0, result.stderr
        assert Path(pairs(result.stdout)["PYW"]).resolve() == runtime_pythonw().resolve()

        # 双边不同目录 fail-closed
        mismatch = env.copy()
        mismatch["ZHULONG_PYTHONW"] = str(pythonw)
        assert run_ps(FIND, mismatch).returncode != 0

        # 显式覆盖到不存在的路径必须失败，不能静默换一套解释器
        bad_env = env.copy()
        bad_env["ZHULONG_PYTHON"] = str(td / "missing" / "python.exe")
        bad_env["ZHULONG_PYTHONW"] = str(td / "missing" / "pythonw.exe")
        assert run_ps(FIND, bad_env).returncode != 0

        # 自动候选：首个是损坏解释器，应继续尝试第二个有效安装
        broken = td / "broken"
        broken.mkdir()
        (broken / "python.exe").write_text("not an executable", encoding="utf-8")
        (broken / "pythonw.exe").write_text("not an executable", encoding="utf-8")
        auto = env.copy()
        auto.pop("ZHULONG_PYTHON", None)
        auto.pop("ZHULONG_PYTHONW", None)
        auto["PATH"] = f"{broken};{Path(sys.executable).parent}"
        auto["LOCALAPPDATA"] = str(td / "no-local")
        auto["ProgramFiles"] = str(td / "no-program-files")
        result = run_ps(FIND, auto)
        assert result.returncode == 0, result.stderr
        assert Path(pairs(result.stdout)["PY"]).resolve() == Path(sys.executable).resolve()

        # XML 生成：特殊字符必须转义且三份任务均可解析
        env["ZHULONG_PYTHONW"] = r"C:\Custom & Python\pythonw.exe"
        env["ZHULONG_PROJECT"] = r"D:\A&B\烛龙"
        env["ZHULONG_TASK_XML_DIR"] = str(td)
        result = run_ps(PREPARE, env)
        assert result.returncode == 0, result.stderr
        for name in ("zhulong_autostart.xml", "zhulong_watchdog.xml", "zhulong_ac7.xml"):
            root = ET.parse(td / name).getroot()
            assert root.find(".//{*}Command").text == env["ZHULONG_PYTHONW"]
            assert root.find(".//{*}WorkingDirectory").text == env["ZHULONG_PROJECT"]
    print("PASS")


if __name__ == "__main__":
    main()

"""用已核验的Inno Setup编译共享运行产物；不安装或启动程序。"""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess

ROOT = Path(__file__).resolve().parent.parent
VERSION = "0.2.0-replacement"
SOURCE_SUFFIXES = {".cs", ".csproj", ".sln", ".py", ".pyc", ".pdb"}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--runtime", type=Path, default=ROOT / "dist/Zhulong")
    parser.add_argument("--compiler", type=Path, default=ROOT / "out/installer-tools/compiler/ISCC.exe")
    parser.add_argument("--output", type=Path, default=ROOT / "dist")
    parser.add_argument("--task-cleanup", action="store_true",
                        help="仅在程序已实现并验证--uninstall-tasks后启用卸载任务清理")
    args = parser.parse_args()
    runtime = args.runtime.resolve()
    compiler = args.compiler.resolve()
    output = args.output.resolve()
    if not compiler.is_file():
        raise SystemExit("缺少已核验的ISCC.exe；编译器仅放开发工具目录，不进入发行包。")
    for name in ("Zhulong.exe", "Web/index.html", "Assets/zhulong-next.ico", "runtime-manifest.json"):
        if not (runtime / name).is_file():
            raise SystemExit("运行产物缺少：" + name)
    manifest = json.loads((runtime / "runtime-manifest.json").read_text(encoding="utf-8-sig"))
    if manifest.get("version") != VERSION or manifest.get("mode") != "portable":
        raise SystemExit("请先由build.py生成同版本便携运行产物：" + VERSION)
    checked = 0
    for name, expected in manifest["files"].items():
        file = (runtime / name).resolve()
        if not file.is_relative_to(runtime):
            raise SystemExit("运行清单路径超出产物目录：" + name)
        parts = file.relative_to(runtime).parts
        if any(part.casefold() in {"data", "updates"} for part in parts) or file.name.casefold() == "installation.json":
            continue
        if file.suffix.casefold() in SOURCE_SUFFIXES:
            raise SystemExit("运行清单混入源码或调试文件：" + name)
        if not file.is_file() or hashlib.sha256(file.read_bytes()).hexdigest() != expected["sha256"]:
            raise SystemExit("运行产物与清单不一致：" + name)
        checked += 1
    output.mkdir(parents=True, exist_ok=True)
    command = [str(compiler), "/Q", "/DRuntimeDir=" + str(runtime),
               "/DOutputDir=" + str(output), "/DAppVersion=" + VERSION]
    if args.task_cleanup:
        command.append("/DEnableOwnedTaskCleanup")
    command.append(str(ROOT / "desktop/installer.iss"))
    subprocess.run(command, check=True)
    installer = output / ("Zhulong-" + VERSION + "-setup.exe")
    if not installer.is_file():
        raise SystemExit("编译器未生成预期安装包。")
    print(json.dumps({"installer": str(installer), "version": VERSION,
                      "app_id": "{4332A2F6-91D8-4441-897C-77A711B7E568}",
                      "runtime_files_verified": checked, "task_cleanup": args.task_cleanup,
                      "size": installer.stat().st_size,
                      "sha256": hashlib.sha256(installer.read_bytes()).hexdigest()}, ensure_ascii=False))


if __name__ == "__main__":
    main()

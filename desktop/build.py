"""构建独立便携运行目录；不修改旧烛龙或用户数据。"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import zipfile

ROOT = Path(__file__).resolve().parent.parent
PROJECT = ROOT / "desktop" / "Zhulong.Desktop"
WEB_SOURCE = PROJECT / "Web"
VERSION = "0.2.7"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, default=ROOT / "dist" / "Zhulong")
    parser.add_argument("--sdk", type=Path, default=ROOT / "out/p0-tools/dotnet-local/dotnet.exe")
    args = parser.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    environment = os.environ.copy()
    environment.update(DOTNET_ROOT=str(args.sdk.resolve().parent),
                       DOTNET_CLI_HOME=str(ROOT / "out/p0-tools/dotnet-home"),
                       NUGET_PACKAGES=str(ROOT / "out/p0-tools/nuget"),
                       DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_SKIP_FIRST_TIME_EXPERIENCE="1")
    subprocess.run([str(args.sdk), "publish", str(PROJECT / "Zhulong.Desktop.csproj"),
                    "-c", "Release", "-r", "win-x64", "--self-contained", "true", "-o", str(output),
                    "-p:Version=" + VERSION, "-p:UseSharedCompilation=false", "-p:DebugType=None"], check=True, env=environment)
    web = output / "Web"
    web.mkdir(exist_ok=True)
    html = (WEB_SOURCE / "index.html").read_text(encoding="utf-8")
    names = re.findall(r'(?:src|href)="([^"#]+\.(?:css|js))"', html)
    for name in names:
        shutil.copy2(WEB_SOURCE / name, web / name)
    assets = web / "assets"
    assets.mkdir(exist_ok=True)
    # 仅当前已选两组昼夜图和品牌，不分发候选图、源工程或设计记录。
    for name in ["ink-day-v7.png", "ink-night-v7.png", "ink-header-day-v3.png", "ink-header-night-v3.png", "zhulong-coiled-mark.svg"]:
        shutil.copy2(WEB_SOURCE / "assets" / name, assets / name)
    shutil.copy2(PROJECT / "Assets" / "zhulong-next.png", assets / "zhulong-next.png")
    html = html.replace('href="assets/zhulong-coiled-mark.svg" type="image/svg+xml"', 'href="assets/zhulong-next.png" type="image/png"')
    html = re.sub(r'(<span class="brand-seal"[^>]*>).*?(</span>)', r'\1<img src="assets/zhulong-next.png" alt="" aria-hidden="true">\2', html, flags=re.S)
    for name in ("live.js", "live.css", "portal.js"):
        shutil.copy2(PROJECT / "Web" / name, web / name)
    html = html.replace("</head>", '<link rel="stylesheet" href="live.css"><script src="portal.js" defer></script><script src="live.js" defer></script></head>')
    html = html.replace("<title>烛龙 · 墨气与太极</title>", "<title>烛龙新版 · 朱印</title>")
    (web / "index.html").write_text(html, encoding="utf-8", newline="\n")
    shutil.copy2(ROOT / "desktop/Zhulong.Runtime/Providers/AI/THIRD_PARTY_NOTICES.md", output / "THIRD_PARTY_NOTICES.md")
    shutil.copy2(ROOT / "LICENSE", output / "LICENSE")
    (output / "使用说明.txt").write_text(
        "烛龙新版替代候选版（朱红方印图标，便携）\n双击 Zhulong.exe；关闭窗口后留在托盘，托盘菜单可打开或彻底退出。\n"
        "数据在data目录，升级保留该目录；运行无需源码、Python或Node。\n"
        "需要系统已有Microsoft Edge WebView2 Runtime；本包不会自动安装。\n"
        "本程序独立采样，不连接旧烛龙；托盘提供设置、暂停、限流、导入及按需浏览器入口。\n", encoding="utf-8", newline="\n")
    files = {}
    for file in output.rglob("*"):
        if file.is_file() and not any(part.casefold() in {"data", "updates"} for part in file.relative_to(output).parts) and file.name not in {"runtime-manifest.json", "installation.json"}:
            files[file.relative_to(output).as_posix()] = {"size": file.stat().st_size, "sha256": hashlib.sha256(file.read_bytes()).hexdigest()}
    (output / "runtime-manifest.json").write_text(json.dumps({"version": VERSION, "mode": "portable", "files": files}, ensure_ascii=False, indent=2), encoding="utf-8", newline="\n")
    archive = output.parent / f"Zhulong-{VERSION}-win-x64.zip"
    with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as package:
        for name in (*files, "runtime-manifest.json"):
            package.write(output / name, "Zhulong/" + name)
    print(json.dumps({"runtime": str(output), "archive": str(archive), "files": len(files), "bytes": sum(item["size"] for item in files.values())}, ensure_ascii=False))


if __name__ == "__main__":
    main()

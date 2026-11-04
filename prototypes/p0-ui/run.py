"""P0 外部测量；只跟踪本脚本启动的原型，不读取生产数据库。"""
import argparse
import csv
import json
import math
import os
from pathlib import Path
import queue
import statistics
import subprocess
import threading
import time
from datetime import datetime, timezone

import psutil

ROOT = Path(__file__).resolve().parent
REPO = ROOT.parents[1]


def percentile(values, fraction):
    values = sorted(values)
    return values[max(0, math.ceil(len(values) * fraction) - 1)] if values else None


def read_output(stream, messages):
    for line in stream:
        messages.put(line.rstrip())


def sample_family(root, known, previous_cpu, interval):
    try:
        for proc in [root, *root.children(recursive=True)]:
            known[(proc.pid, proc.create_time())] = proc
    except psutil.Error:
        pass
    totals = dict(private_mb=0.0, working_mb=0.0, handles=0, processes=0,
                  read_bytes=0, write_bytes=0, cpu_one_core_percent=0.0)
    for identity, proc in list(known.items()):
        try:
            if proc.create_time() != identity[1] or not proc.is_running():
                continue
            with proc.oneshot():
                mem = proc.memory_info()
                cpu = sum(proc.cpu_times()[:2])
                io = proc.io_counters()
                totals["private_mb"] += mem.private / 1024**2
                totals["working_mb"] += mem.rss / 1024**2
                totals["handles"] += proc.num_handles()
                totals["processes"] += 1
                totals["read_bytes"] += io.read_bytes
                totals["write_bytes"] += io.write_bytes
                totals["cpu_one_core_percent"] += max(0, cpu - previous_cpu.get(identity, 0)) / interval * 100
                previous_cpu[identity] = cpu
        except (psutil.NoSuchProcess, psutil.ZombieProcess):
            continue
    totals["cpu_machine_percent"] = totals["cpu_one_core_percent"] / (psutil.cpu_count() or 1)
    return totals


def survivors(known):
    alive = []
    for (pid, created), proc in known.items():
        try:
            if proc.is_running() and proc.create_time() == created:
                alive.append(proc)
        except psutil.Error:
            pass
    return alive


def run_case(args, mode, failure=False):
    label = mode + ("-missing-assets" if failure else "")
    output = args.output / label
    output.mkdir(parents=True)
    environment = os.environ.copy()
    environment.update(DOTNET_ROOT=str(args.dotnet.parent),
                       DOTNET_CLI_TELEMETRY_OPTOUT="1",
                       DOTNET_SKIP_FIRST_TIME_EXPERIENCE="1")
    assets = ROOT / ("missing-web-assets" if failure else "web-dist")
    command = [str(args.dotnet), str(ROOT / "bin/Release/net10.0-windows/P0.Ui.dll"),
               "--mode", mode, "--cycles", str(1 if failure else args.cycles),
               "--seconds", str(1 if failure else args.seconds),
               "--assets", str(assets), "--profile", str(output / "web-profile")]
    if args.capture and not failure:
        command += ["--capture", str(output / "panel.png")]
    started = time.monotonic()
    process = subprocess.Popen(command, cwd=ROOT, env=environment,
                               stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                               text=True, encoding="utf-8")
    root = psutil.Process(process.pid)
    known = {(root.pid, root.create_time()): root}
    messages = queue.Queue()
    reader = threading.Thread(target=read_output, args=(process.stdout, messages), daemon=True)
    reader.start()
    events, rows, previous_cpu = [], [], {}
    phase, last_sample = "startup", started
    next_sample = started
    timeout = started + 180 + args.cycles * 30 + args.seconds * 5
    try:
        while process.poll() is None or reader.is_alive() or not messages.empty():
            now = time.monotonic()
            if now > timeout:
                raise TimeoutError(f"{label} exceeded bounded experiment time")
            try:
                line = messages.get(timeout=0.1)
                event = json.loads(line)
                events.append(event)
                if event.get("kind") == "phase":
                    phase = event["name"]
                    print(label, phase, flush=True)
                elif event.get("kind") == "webview":
                    try:
                        browser = psutil.Process(event["browserPid"])
                        marker = str(output / "web-profile").casefold()
                        if any(marker in arg.casefold() for arg in browser.cmdline()):
                            for child in [browser, *browser.children(recursive=True)]:
                                known[(child.pid, child.create_time())] = child
                    except psutil.NoSuchProcess:
                        pass
                elif event.get("kind") == "cycle" and event["number"] % 25 == 0:
                    print(label, "cycles", event["number"], flush=True)
                elif event.get("kind") in ("failure", "cleanupFailure"):
                    print(label, event, flush=True)
            except queue.Empty:
                pass
            except json.JSONDecodeError:
                events.append({"kind": "output", "text": line})
            if now >= next_sample:
                row = sample_family(root, known, previous_cpu, max(now - last_sample, 0.01))
                row.update(elapsed_s=now - started, phase=phase)
                rows.append(row)
                last_sample, next_sample = now, now + 0.5
        process.wait(timeout=10)
        # 观察窗口结束后仍存活的已知进程；此时不主动结束来伪造通过。
        deadline = time.monotonic() + 10
        while survivors(known) and time.monotonic() < deadline:
            time.sleep(0.2)
        # 独立用户目录补查已脱离父链、可能错过采样的浏览器进程。
        profile_marker = str(output / "web-profile").casefold()
        for candidate in psutil.process_iter(["pid", "name", "cmdline", "create_time"]):
            if (candidate.info["name"] or "").lower() == "msedgewebview2.exe" and any(
                profile_marker in arg.casefold() for arg in candidate.info["cmdline"] or []
            ):
                known[(candidate.pid, candidate.info["create_time"])] = candidate
        remaining = survivors(known)
        cycle_count = sum(e.get("kind") == "cycle" for e in events)
        failures = [e for e in events if e.get("kind") in ("failure", "cleanupFailure")]
        passed = (process.returncode == 0 and not remaining and
                  cycle_count == args.cycles and any(e.get("kind") == "done" for e in events))
        if failure:
            passed = (process.returncode != 0 and not remaining and
                      any("前端资源缺失" in e.get("error", "") for e in failures))
        summary = dict(mode=mode, expected_failure=failure, passed=passed,
                       exit_code=process.returncode, cycles=cycle_count,
                       surviving_pids=[p.pid for p in remaining], phases={})
        # 阶段前 5 秒视为转换期；仅统计后面的稳定样本。
        for name in ("before", "open", "closed", "after"):
            selected = [r for r in rows if r["phase"] == name]
            if not selected:
                continue
            settled = [r for r in selected if r["elapsed_s"] >= selected[0]["elapsed_s"] + 5]
            summary["phases"][name] = {
                key: round(statistics.mean(r[key] for r in settled), 3)
                for key in ("private_mb", "working_mb", "handles", "processes",
                            "cpu_one_core_percent", "cpu_machine_percent")
            } if settled else {}
        for kind in ("open", "refresh", "close"):
            values = [e["ms"] for e in events if e.get("kind") == kind]
            summary[kind + "_ms"] = {
                "count": len(values), "first": values[0] if values else None,
                "median": statistics.median(values) if values else None,
                "p95": percentile(values, 0.95), "max": max(values) if values else None}
        (output / "events.json").write_text(json.dumps(events, ensure_ascii=False, indent=2), encoding="utf-8")
        if rows:
            with (output / "samples.csv").open("w", encoding="utf-8", newline="") as stream:
                writer = csv.DictWriter(stream, fieldnames=list(rows[0]))
                writer.writeheader()
                writer.writerows(rows)
        (output / "summary.json").write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding="utf-8")
        print(label, "PASS" if passed else "FAIL", flush=True)
        return summary
    finally:
        # 异常时只回收本轮确认身份的原型进程；有清理需求的样本不会判成功。
        for proc in reversed(survivors(known)):
            try:
                proc.kill()
            except psutil.Error:
                pass
        process.wait(timeout=10)
        reader.join(timeout=2)
        process.stdout.close()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--dotnet", type=Path, default=REPO / "out/p0-tools/dotnet-local/dotnet.exe")
    parser.add_argument("--output", type=Path)
    parser.add_argument("--cycles", type=int, default=0)
    parser.add_argument("--capture", action="store_true")
    parser.add_argument("--seconds", type=int, default=20)
    parser.add_argument("--mode", choices=("all", "wpf", "hybrid"), default="all")
    args = parser.parse_args()
    stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    args.output = (args.output or REPO / "out/p0-ui" / stamp).resolve()
    args.output.mkdir(parents=True, exist_ok=False)
    modes = ("wpf", "hybrid") if args.mode == "all" else (args.mode,)
    results = [run_case(args, mode) for mode in modes]
    if "hybrid" in modes:
        results.append(run_case(args, "hybrid", failure=True))
    (args.output / "results.json").write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding="utf-8")
    print("results", args.output, flush=True)
    raise SystemExit(0 if all(r["passed"] for r in results) else 1)


if __name__ == "__main__":
    main()

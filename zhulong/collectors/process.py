"""白名单进程 commit 采集：只看 config.WATCH_PROCESSES 中列出的进程。
v1.7 池泄洪：同名多规则按 path_contains 分桶（exe 读不到的进程不进任何带路径的
组——fail-closed，宁水位低估不误匹配）；带 group_commit_gb 的规则落聚合值。"""
import psutil

from zhulong import config as default_config

GB = 1024 ** 3


def _commit_gb(info):
    if "memory_commit" in info:
        return info["memory_commit"] / GB
    # Windows 语义：psutil 无 per-process commit 专用字段时，memory_info().vms
    # 即 PagefileUsage（进程 commit charge），作为回退
    mi = info.get("memory_info")
    if mi is None:
        return None   # 竞态：process_iter(attrs) 途中进程退出
    commit = getattr(mi, "commit", None)
    if not commit:
        commit = mi.vms
    return commit / GB


def collect(cfg, process_iter=None):
    cfg = cfg or default_config
    rules_by_name = {}
    for w in cfg.WATCH_PROCESSES:
        rules_by_name.setdefault(w["name"].lower(), []).append(w)
    rows = []
    if process_iter is not None:
        procs = process_iter()
    else:
        procs = psutil.process_iter(["pid", "name", "memory_info", "exe"])
    # 同名进程先收齐（pid, commit_gb, exe），再逐规则分桶
    by_name = {}
    for p in procs:
        name = (p.info.get("name") or "").lower()
        if name not in rules_by_name:
            continue
        gb = _commit_gb(p.info)
        if gb is None:
            continue
        by_name.setdefault(name, []).append((p.info.get("pid"), gb, p.info.get("exe")))
    for name, rules in rules_by_name.items():
        members = by_name.get(name, [])
        for rule in rules:
            pc = rule.get("path_contains")
            group = ([m for m in members if m[2] and pc.lower() in str(m[2]).lower()]
                     if pc else members)
            key = f"{default_config.watch_key(rule)}_commit_gb"
            if not group:
                rows.append((key, 0.0, "absent"))
            elif rule.get("group_commit_gb") is not None:
                rows.append((key, round(sum(g for _, g, _ in group), 2), f"{len(group)}个"))
            else:
                pid, gb, _ = max(group, key=lambda m: m[1])
                rows.append((key, round(gb, 2), str(pid)))
    return rows


COLLECTOR = {"name": "process", "interval": 1, "collect": collect}

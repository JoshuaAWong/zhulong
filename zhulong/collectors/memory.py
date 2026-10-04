"""系统内存/commit 采集。
commit charge（提交内存）数据源：ctypes 调 GetPerformanceInfo，与 PowerShell
Get-Counter "\\Memory\\Committed Bytes"/"\\Memory\\Commit Limit" 完全同源。

注意（勿改回 psutil）：新版 psutil 的 swap_memory() 在 Windows 上返回的是
页面文件(pagefile)用量而非 commit charge——本机实测 total=62GB(页面文件) vs
系统 commit 上限 93GB。旧版 psutil 曾用它表示 commit，行为已在更新中改变。"""
import ctypes
from ctypes import wintypes

import psutil

GB = 1024 ** 3


class _PERF_INFO(ctypes.Structure):
    _fields_ = [("cb", wintypes.DWORD), ("CommitTotal", ctypes.c_size_t),
                ("CommitLimit", ctypes.c_size_t), ("CommitPeak", ctypes.c_size_t),
                ("PhysicalTotal", ctypes.c_size_t), ("PhysicalAvailable", ctypes.c_size_t),
                ("SystemCache", ctypes.c_size_t), ("KernelTotal", ctypes.c_size_t),
                ("KernelPaged", ctypes.c_size_t), ("KernelNonpaged", ctypes.c_size_t),
                ("PageSize", ctypes.c_size_t), ("HandleCount", wintypes.DWORD),
                ("ProcessCount", wintypes.DWORD), ("ThreadCount", wintypes.DWORD)]


def _commit_stats():
    """返回 (used_bytes, limit_bytes)，数据源 GetPerformanceInfo。"""
    pi = _PERF_INFO()
    pi.cb = ctypes.sizeof(pi)
    ctypes.windll.psapi.GetPerformanceInfo(ctypes.byref(pi), pi.cb)
    return pi.CommitTotal * pi.PageSize, pi.CommitLimit * pi.PageSize


def collect(cfg, commit_stats=None, vm=None, pagefile=None):
    used, limit = (commit_stats or _commit_stats)()
    v = vm or psutil.virtual_memory()
    pf = pagefile or psutil.swap_memory()   # 新版 psutil：此处即页面文件用量
    return [
        ("commit_used_gb", round(used / GB, 2), ""),
        ("commit_limit_gb", round(limit / GB, 2), ""),
        ("commit_percent", round(used / limit * 100, 1), ""),
        ("mem_percent", round(v.percent, 1), ""),
        ("mem_used_gb", round(v.used / GB, 2), ""),
        ("mem_total_gb", round(v.total / GB, 2), ""),
        ("pagefile_used_gb", round(pf.used / GB, 2), ""),
        ("pagefile_total_gb", round(pf.total / GB, 2), ""),
    ]


COLLECTOR = {"name": "memory", "interval": 1, "collect": collect}

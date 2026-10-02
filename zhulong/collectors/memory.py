r"""系统内存/commit 采集。
Windows 语义（勿改）：psutil.swap_memory() 在 Windows 上返回的就是 commit charge——
total=CommitLimit×PageSize、used=CommitTotal×PageSize，与 PowerShell
Get-Counter "\Memory\Committed Bytes"/"\Memory\Commit Limit" 完全对齐。
它不是 pagefile 使用量；sin/sout 在 Windows 恒为 0，不使用。"""
import psutil

GB = 1024 ** 3


def collect(cfg, swap=None, vm=None):
    s = swap or psutil.swap_memory()
    v = vm or psutil.virtual_memory()
    return [
        ("commit_used_gb", round(s.used / GB, 2), ""),
        ("commit_limit_gb", round(s.total / GB, 2), ""),
        ("commit_percent", round(s.percent, 1), ""),
        ("mem_percent", round(v.percent, 1), ""),
    ]


COLLECTOR = {"name": "memory", "interval": 1, "collect": collect}

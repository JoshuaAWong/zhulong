from zhulong.collectors import memory, process

GB = 1024 ** 3


class FakeVM:
    percent = 42.0
    used = 13.4 * GB
    total = 32 * GB


class FakePagefile:
    used = 2 * GB
    total = 64 * GB


class FakeProc:
    def __init__(self, pid, name, rss_commit_bytes):
        self.info = {"pid": pid, "name": name, "memory_commit": rss_commit_bytes}


def main():
    # memory：注入 fake（commit_stats/vm/pagefile）
    fake_stats = lambda: (50 * GB, 100 * GB)
    rows = memory.collect(None, commit_stats=fake_stats, vm=FakeVM(), pagefile=FakePagefile())
    keys = {k for k, v, l in rows}
    assert keys == {"commit_used_gb", "commit_limit_gb", "commit_percent", "mem_percent",
                    "mem_used_gb", "mem_total_gb", "pagefile_used_gb", "pagefile_total_gb"}, keys
    d = {k: v for k, v, l in rows}
    assert abs(d["commit_limit_gb"] - 100.0) < 0.01
    assert abs(d["commit_percent"] - 50.0) < 0.01
    assert abs(d["mem_used_gb"] - 13.4) < 0.01
    assert abs(d["pagefile_used_gb"] - 2.0) < 0.01
    # 真实 _commit_stats 形态校验（调用真实 GetPerformanceInfo，值域合理即可）
    used, limit = memory._commit_stats()
    assert 0 < used < limit, (used, limit)

    # process：注入 fake process_iter
    fake_iter = lambda: [FakeProc(111, "HYPHelper.exe", 16 * 2**30),
                         FakeProc(222, "notepad.exe", 1 * 2**30)]
    rows = process.collect(None, process_iter=fake_iter)
    d = {k: (v, l) for k, v, l in rows}
    assert abs(d["HYPHelper_commit_gb"][0] - 16.0) < 0.01 and d["HYPHelper_commit_gb"][1] == "111", d
    assert "notepad_commit_gb" not in d or d["notepad_commit_gb"][1] == "absent"
    # cpu：注入 fake（CPU_TOP_INTERVAL=1 使大户每轮必出）
    from zhulong.collectors import cpu, diskio

    class CC: CPU_TOP_INTERVAL = 1
    rows = cpu.collect(CC, cpu_percent=33.3, percpu=[10.0, 90.0, 20.0], top_fn=lambda c: ("evil.exe", 88.0))
    d = {k: (v, l) for k, v, l in rows}
    assert d["cpu_percent"][0] == 33.3 and d["cpu_max_core"][0] == 90.0, d
    assert d["cpu_top_pct"] == (88.0, "evil.exe"), d

    # diskio：注入 fake
    rows = diskio.collect(CC, rates_fn=lambda: (12.5, 3.5, "E"), removable_fn=lambda: "E",
                          top_fn=lambda c: ("scanner.exe", 500.0))
    d = {k: (v, l) for k, v, l in rows}
    assert d["io_read_mb_s"] == (12.5, "E") and d["io_write_mb_s"] == (3.5, "E"), d
    assert d["io_remount"] == (1.0, "E"), d
    assert d["io_top_mb"] == (500.0, "scanner.exe"), d
    print("PASS")


if __name__ == "__main__":
    main()

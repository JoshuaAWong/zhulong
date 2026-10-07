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

    class CC: CPU_TOP_INTERVAL = 1; GPU_INTERVAL = 1
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

    # net：注入 fake（速率/ping/网关）
    from zhulong.collectors import net, gpu
    net._PINGS["net"] = [10.0, 12.0, 14.0]
    rows = net.collect(CC, rates_fn=lambda: (2.5, 0.4), ping_fn=lambda h: 12.0 if h == "223.5.5.5" else 0.6,
                       gw_fn=lambda: "192.168.1.1")
    d = {k: v for k, v, l in rows}
    assert d["net_down_mb_s"] == 2.5 and d["net_up_mb_s"] == 0.4, d
    assert d["ping_gw_ms"] == 0.6 and d["ping_net_ms"] == 12.0, d
    assert d["ping_jitter_ms"] > 0, d

    # gpu：注入 fake（分频 CC 无 GPU_INTERVAL → 默认 2，首轮即采）
    rows = gpu.collect(CC, query_fn=lambda: (69.0, 94.0, 4.8, 11.9))
    d = {k: v for k, v, l in rows}
    assert d == {"gpu_temp": 69.0, "gpu_util": 94.0, "gpu_mem_used": 4.8, "gpu_mem_total": 11.9}, d
    print("PASS")


if __name__ == "__main__":
    main()

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
    # 常量键 on-change（B3）：同值不再写；变值写；跨天（昨日记录）即使同值也兜底写一行
    rows2 = memory.collect(None, commit_stats=fake_stats, vm=FakeVM(), pagefile=FakePagefile())
    keys2 = {k for k, v, l in rows2}
    assert keys2 == {"commit_used_gb", "commit_percent", "mem_percent",
                    "mem_used_gb", "pagefile_used_gb"}, keys2   # 三个常量键同值被跳过
    memory._LAST["mem_total_gb"] = ("2000-01-01", 32.0)   # 模拟昨日记录（值相同）
    rows3 = memory.collect(None, commit_stats=fake_stats, vm=FakeVM(), pagefile=FakePagefile())
    assert "mem_total_gb" in {k for k, v, l in rows3}   # 每日兜底一行
    class BiggerVM(FakeVM): total = 64 * GB
    rows4 = memory.collect(None, commit_stats=fake_stats, vm=BiggerVM(), pagefile=FakePagefile())
    d4 = {k: v for k, v, l in rows4}
    assert d4.get("mem_total_gb") == 64.0, d4   # 变值即写
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
    # cpu：注入 fake（仅零成本总量）
    from zhulong.collectors import cpu, diskio, proctop

    class CC: pass   # B3 起分频收编调度器（COLLECTOR["interval"]），cfg 不再带分频配置
    rows = cpu.collect(CC, cpu_percent=33.3, percpu=[10.0, 90.0, 20.0])
    d = {k: v for k, v, l in rows}
    assert d == {"cpu_percent": 33.3, "cpu_max_core": 90.0}, d

    # diskio：注入 fake（仅零成本总量）
    rows = diskio.collect(CC, rates_fn=lambda: (12.5, 3.5, "E"), removable_fn=lambda: "E")
    d = {k: (v, l) for k, v, l in rows}
    assert d["io_read_mb_s"] == (12.5, "E") and d["io_write_mb_s"] == (3.5, "E"), d
    assert d["io_remount"] == (1.0, "E"), d

    # proctop：一次遍历产三类大户（scan_fn 直给结果）
    rows = proctop.collect(CC, scan_fn=lambda c: {"cpu": ("evil.exe", 88.0), "io": ("scanner.exe", 500.0),
        "commit": [(31.9, "HYPHelper.exe"), (6.2, "java.exe"), (1.1, "chrome.exe")]})
    d = {k: (v, l) for k, v, l in rows}
    assert d["cpu_top_pct"] == (88.0, "evil.exe"), d
    assert d["io_top_mb"] == (500.0, "scanner.exe"), d
    assert d["proctop1"] == (31.9, "HYPHelper.exe") and d["proctop2"] == (6.2, "java.exe"), d

    # proctop label 截断（B3）：cmdline 超 200 字符截断加省略标记，控制 DB 写入量
    class LongCmdProc:
        def cmdline(self):
            return ["pythonw.exe", "-" + "x" * 500]
    proctop._PROC_CACHE[999] = LongCmdProc()
    try:
        rows = proctop.collect(CC, scan_fn=lambda c: {"cpu": ("", 0.0), "io": ("", 0.0),
            "commit": [(10.0, "long.exe", 999)]})
        label = {k: l for k, v, l in rows}["proctop1"]
        cmd = label.split("|", 2)[2]
        assert len(cmd) == 201 and cmd.endswith("…"), (len(cmd), cmd[-10:])
    finally:
        proctop._PROC_CACHE.pop(999, None)

    # net：注入 fake（速率/ping/网关）
    from zhulong.collectors import net, gpu
    net._PINGS["net"] = [10.0, 12.0, 14.0]
    rows = net.collect(CC, rates_fn=lambda: (2.5, 0.4), ping_fn=lambda h: 12.0 if h == "223.5.5.5" else 0.6,
                       gw_fn=lambda: "192.168.1.1")
    d = {k: v for k, v, l in rows}
    assert d["net_down_mb_s"] == 2.5 and d["net_up_mb_s"] == 0.4, d
    assert d["ping_gw_ms"] == 0.6 and d["ping_net_ms"] == 12.0, d
    assert d["ping_jitter_ms"] > 0, d

    # net._parse_default_gateway：假 route print 输出（注入假输出，不依赖实机）
    fake_out = (
        "===========================================================================\n"
        "IPv4 路由表\n"
        "===========================================================================\n"
        "活动路由:\n"
        "网络目标        网络掩码          网关             接口          跃点数\n"
        "          0.0.0.0          0.0.0.0      192.168.1.1    192.168.1.100     25\n"
        "          0.0.0.0          0.0.0.0         10.8.0.1       10.8.0.6       10\n"
        "        127.0.0.0        255.0.0.0        在链路上       127.0.0.1        331\n"
        "===========================================================================\n"
    )
    assert net._parse_default_gateway(fake_out) == "10.8.0.1"   # 多条默认路由取跃点数最小者
    assert net._parse_default_gateway("信息: 没有可用的路由。\r\n") is None
    # 默认路由网关列为「在链路上」（非 IP）→ 跳过，不可 ping
    assert net._parse_default_gateway(
        "          0.0.0.0          0.0.0.0        在链路上   192.168.1.100     25\n") is None

    # 网关缓存：命中不重新解析；ping 网关失败才重新解析 route print
    calls = []
    orig_gw = net._gateway
    net._gateway = lambda: calls.append(1) or "192.168.1.1"
    try:
        net._GW_CACHE["ip"] = None
        net.collect(CC, rates_fn=lambda: (0.0, 0.0), ping_fn=lambda h: 1.0)
        assert len(calls) == 1, calls   # 首次解析
        net.collect(CC, rates_fn=lambda: (0.0, 0.0), ping_fn=lambda h: 1.0)
        assert len(calls) == 1, calls   # 缓存命中，未再解析
        net.collect(CC, rates_fn=lambda: (0.0, 0.0), ping_fn=lambda h: None)
        assert len(calls) == 2, calls   # ping 网关失败 → 重新解析
    finally:
        net._gateway = orig_gw
        net._GW_CACHE["ip"] = None

    # gpu：注入 fake（分频已收编调度器，collect 每次调用即采）
    rows = gpu.collect(CC, query_fn=lambda: (69.0, 94.0, 4.8, 11.9))
    d = {k: v for k, v, l in rows}
    assert d == {"gpu_temp": 69.0, "gpu_util": 94.0, "gpu_mem_used": 4.8, "gpu_mem_total": 11.9}, d
    # gpu_mem_total on-change：同值不再写，变值写
    rows = gpu.collect(CC, query_fn=lambda: (69.0, 94.0, 4.8, 11.9))
    assert {k for k, v, l in rows} == {"gpu_temp", "gpu_util", "gpu_mem_used"}, rows
    rows = gpu.collect(CC, query_fn=lambda: (69.0, 94.0, 4.8, 24.0))
    assert "gpu_mem_total" in {k for k, v, l in rows}, rows

    # gpu._parse（B3）：多卡取首行；'N/A' 逐字段容错（该字段 None → 跳过不产出）
    assert gpu._parse("69, 94, 4915, 12288\n70, 90, 5000, 12288\n") == (69.0, 94.0, 4915 / 1024, 12.0)
    t, u, mu, mt = gpu._parse("N/A, 50, N/A, 8192\n")
    assert t is None and u == 50.0 and mu is None and mt == 8.0, (t, u, mu, mt)
    rows = gpu.collect(CC, query_fn=lambda: (None, 50.0, None, 24.0))   # mt 同上轮（24.0）不重复写
    assert {k for k, v, l in rows} == {"gpu_util"}, rows   # N/A 字段跳过，整组不炸
    assert gpu._parse("") is None and gpu._parse("garbage\n") is None

    # gpu 无 N 卡负缓存：FileNotFoundError 后永久跳过，不再重试
    calls = []
    def _no_smi(*a, **k):
        calls.append(1)
        raise FileNotFoundError("nvidia-smi")
    orig_run = gpu.subprocess.run
    gpu.subprocess.run = _no_smi
    try:
        assert gpu.collect(CC) == [] and gpu._NO_GPU and len(calls) == 1
        assert gpu.collect(CC) == [] and len(calls) == 1, calls   # 负缓存生效，未再调 subprocess
    finally:
        gpu.subprocess.run = orig_run
        gpu._NO_GPU = False
    print("PASS")


if __name__ == "__main__":
    main()

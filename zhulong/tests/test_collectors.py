from zhulong.collectors import memory, process


class FakeSwap:
    def __init__(self, total, used):
        self.total, self.used, self.percent = total, used, used / total * 100


class FakeVM:
    percent = 42.0


class FakeProc:
    def __init__(self, pid, name, rss_commit_bytes):
        self.info = {"pid": pid, "name": name, "memory_commit": rss_commit_bytes}


def main():
    # memory：注入 fake psutil
    rows = memory.collect(None, swap=FakeSwap(100 * 2**30, 50 * 2**30), vm=FakeVM())
    keys = {k for k, v, l in rows}
    assert keys == {"commit_used_gb", "commit_limit_gb", "commit_percent", "mem_percent"}, keys
    d = {k: v for k, v, l in rows}
    assert abs(d["commit_limit_gb"] - 100.0) < 0.01
    assert abs(d["commit_percent"] - 50.0) < 0.01

    # process：注入 fake process_iter
    fake_iter = lambda: [FakeProc(111, "HYPHelper.exe", 16 * 2**30),
                         FakeProc(222, "notepad.exe", 1 * 2**30)]
    rows = process.collect(None, process_iter=fake_iter)
    d = {k: (v, l) for k, v, l in rows}
    assert abs(d["HYPHelper_commit_gb"][0] - 16.0) < 0.01 and d["HYPHelper_commit_gb"][1] == "111", d
    assert "notepad_commit_gb" not in d or d["notepad_commit_gb"][1] == "absent"
    print("PASS")


if __name__ == "__main__":
    main()

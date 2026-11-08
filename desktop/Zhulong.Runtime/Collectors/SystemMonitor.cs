using System.ComponentModel;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Zhulong.Core;

namespace Zhulong.Runtime.Collectors;

// 快采只读取本机计数器；命令、探测和磁盘容量由独立慢采更新。
public sealed class SystemMonitor : IDisposable
{
    readonly RuntimeSettings settings;
    readonly SlowMetrics slow;
    NativeCounters.CoreTimes[] previousCores = [];
    Dictionary<int, ProcessCounter> previousProcesses = [];
    Dictionary<string, NetworkCounter> previousNetwork = [];
    int logicalProcessors = Math.Max(1, Environment.ProcessorCount);
    public SystemMonitor(RuntimeSettings? settings = null)
    {
        this.settings = settings ?? new RuntimeSettings();
        slow = new SlowMetrics(this.settings);
    }
    public Task CollectSlowAsync(CancellationToken cancellationToken) => slow.CollectAsync(cancellationToken);

    public SystemSnapshot Collect()
    {
        var now = DateTimeOffset.UtcNow;
        var metrics = slow.Snapshot(now);
        var errors = new List<string>();
        double? commit = null, commitUsed = null, commitLimit = null, physical = null, physicalUsed = null, physicalTotal = null;
        if (NativeCounters.GetPerformanceInfo(out var info, (uint)Marshal.SizeOf<NativeCounters.PerformanceInfo>()))
        {
            commitUsed = info.CommitTotal.ToUInt64() * (double)info.PageSize.ToUInt64() / NativeCounters.Gb;
            commitLimit = info.CommitLimit.ToUInt64() * (double)info.PageSize.ToUInt64() / NativeCounters.Gb;
            commit = commitLimit > 0 ? commitUsed * 100 / commitLimit : null;
        }
        else errors.Add("提交内存采集失败");
        var memory = new NativeCounters.MemoryStatus { Length = (uint)Marshal.SizeOf<NativeCounters.MemoryStatus>() };
        if (NativeCounters.GlobalMemoryStatusEx(ref memory))
        {
            physical = memory.MemoryLoad;
            physicalUsed = (memory.TotalPhysical - memory.AvailablePhysical) / NativeCounters.Gb;
            physicalTotal = memory.TotalPhysical / NativeCounters.Gb;
        }
        else errors.Add("物理内存采集失败");
        var pagefile = NativeCounters.Pagefile();
        Put(metrics, now, "pagefile_used_gb", pagefile?.UsedGb, "GB", state: pagefile is null ? "unavailable" : "ok");
        Put(metrics, now, "pagefile_total_gb", pagefile?.TotalGb, "GB", state: pagefile is null ? "unavailable" : "ok");
        var (cpu, maxCore, cpuState) = Cpu();
        Put(metrics, now, "cpu_percent", cpu, "%", state: cpuState);
        Put(metrics, now, "cpu_max_core", maxCore, "%", state: cpuState);
        Put(metrics, now, "commit_percent", commit, "%");
        Put(metrics, now, "mem_percent", physical, "%");
        List<ProcessRow> processes;
        try { processes = Processes(); }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        { previousProcesses.Clear(); processes = []; errors.Add("进程排行采集失败"); }
        Network(metrics, now);
        var watch = settings.WatchProcesses.Select(value => value.Name)
            .Concat(settings.TrimProcesses.Select(value => value.Name))
            .Concat(settings.RestartServiceRules.SelectMany(value => value.TriggerProcesses))
            .Concat(settings.AutoKill)
            .Select(name=>name.EndsWith(".exe",StringComparison.OrdinalIgnoreCase)?name:name+".exe").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selected = processes.OrderByDescending(value => value.CommitGb).Take(5)
            .Concat(processes.Where(value => value.CpuPercent.HasValue).OrderByDescending(value => value.CpuPercent).Take(5))
            .Concat(processes.Where(value => value.ReadMbS.HasValue).OrderByDescending(value => value.ReadMbS + value.WriteMbS).Take(5))
            .Concat(processes.Where(value => watch.Contains(value.Name))).DistinctBy(value => value.Pid).ToList();
        var cpuTop = processes.Where(value => value.CpuPercent.HasValue).MaxBy(value => value.CpuPercent);
        var ioTop = processes.Where(value => value.ReadMbS.HasValue).MaxBy(value => value.ReadMbS + value.WriteMbS);
        Put(metrics, now, "cpu_top_pct", cpuTop?.CpuPercent, "%", cpuTop?.Name, cpuTop is null ? processes.Count == 0 ? "unavailable" : "warming" : "ok");
        Put(metrics, now, "io_top_mb_s", ioTop?.ReadMbS + ioTop?.WriteMbS, "MB/s", ioTop?.Name, ioTop is null ? processes.Count == 0 ? "unavailable" : "warming" : "ok");
        return new SystemSnapshot(now, commit, commitUsed, commitLimit, physical, physicalUsed, physicalTotal, cpu,
            processes.OrderByDescending(value => value.MemoryGb).Take(5).ToList(),
            errors.Count == 0 ? null : string.Join("；", errors), metrics, selected);
    }

    (double? Total, double? Maximum, string State) Cpu()
    {
        var current = NativeCounters.CpuTimes();
        double busy = 0, total = 0, maximum = 0;
        var ready = current.Length > 0 && current.Length == previousCores.Length;
        if (ready)
            for (var i = 0; i < current.Length; i++)
            {
                var delta = current[i].Kernel - previousCores[i].Kernel + current[i].User - previousCores[i].User;
                var idle = current[i].Idle - previousCores[i].Idle;
                if (delta <= 0 || idle < 0) { ready = false; break; }
                total += delta;
                busy += Math.Max(0, delta - idle);
                maximum = Math.Max(maximum, Math.Clamp((delta - (double)idle) * 100 / delta, 0, 100));
            }
        previousCores = current;
        if (current.Length > 0) logicalProcessors = current.Length;
        return ready && total > 0 ? (Math.Clamp(busy * 100 / total, 0, 100), maximum, "ok") : (null, null, current.Length == 0 ? "unavailable" : "warming");
    }

    List<ProcessRow> Processes()
    {
        var now = Stopwatch.GetTimestamp();
        var next = new Dictionary<int, ProcessCounter>();
        var rows = new List<ProcessRow>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id == 0) continue;
                    var name = process.ProcessName;
                    if (name is "System" or "Registry" or "Memory Compression") continue;
                    if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name += ".exe";
                    var created = process.StartTime.ToUniversalTime().Ticks;
                    var cpu = process.TotalProcessorTime.Ticks;
                    var hasIo = NativeCounters.GetProcessIoCounters(process.SafeHandle, out var io);
                    var counter = new ProcessCounter(created, cpu, hasIo ? io.ReadBytes : null, hasIo ? io.WriteBytes : null, now);
                    next[process.Id] = counter;
                    double? cpuPercent = null, read = null, write = null;
                    if (previousProcesses.TryGetValue(process.Id, out var previous) && previous.Created == created)
                    {
                        var seconds = Stopwatch.GetElapsedTime(previous.Time, now).TotalSeconds;
                        if (seconds > 0)
                        {
                            cpuPercent = Math.Clamp((cpu - previous.Cpu) / (double)TimeSpan.TicksPerSecond / seconds / logicalProcessors * 100, 0, 100);
                            if (counter.Read is { } readNow && previous.Read is { } readBefore && readNow >= readBefore &&
                                counter.Write is { } writeNow && previous.Write is { } writeBefore && writeNow >= writeBefore)
                            { read = (readNow - readBefore) / NativeCounters.Mb / seconds; write = (writeNow - writeBefore) / NativeCounters.Mb / seconds; }
                        }
                    }
                    rows.Add(new ProcessRow(process.Id, name, process.WorkingSet64 / NativeCounters.Gb, cpuPercent,
                        process.PrivateMemorySize64 / NativeCounters.Gb, read, write, created));
                }
                catch (Exception error) when (error is Win32Exception or InvalidOperationException or NotSupportedException)
                { /* 退出或权限不足的进程不进入本次排行，句柄由using关闭。 */ }
            }
        }
        previousProcesses = next;
        return rows;
    }

    void Network(Dictionary<string, MetricValue> metrics, DateTimeOffset timestamp)
    {
        var now = Stopwatch.GetTimestamp();
        var next = new Dictionary<string, NetworkCounter>();
        double down = 0, up = 0;
        var ready = false;
        try
        {
            foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (network.OperationalStatus != OperationalStatus.Up || network.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var counters = network.GetIPStatistics();
                next[network.Id] = new(counters.BytesReceived, counters.BytesSent, now);
                if (!previousNetwork.TryGetValue(network.Id, out var previous)) continue;
                var seconds = Stopwatch.GetElapsedTime(previous.Time, now).TotalSeconds;
                if (seconds <= 0 || counters.BytesReceived < previous.Read || counters.BytesSent < previous.Write) continue;
                down += (counters.BytesReceived - previous.Read) / NativeCounters.Mb / seconds;
                up += (counters.BytesSent - previous.Write) / NativeCounters.Mb / seconds;
                ready = true;
            }
            Put(metrics, timestamp, "net_down_mb_s", ready ? down : null, "MB/s", state: ready ? "ok" : "warming");
            Put(metrics, timestamp, "net_up_mb_s", ready ? up : null, "MB/s", state: ready ? "ok" : "warming");
        }
        catch (NetworkInformationException)
        {
            Put(metrics, timestamp, "net_down_mb_s", null, "MB/s", state: "unavailable");
            Put(metrics, timestamp, "net_up_mb_s", null, "MB/s", state: "unavailable");
        }
        previousNetwork = next;
    }

    internal static void Put(Dictionary<string, MetricValue> metrics, DateTimeOffset timestamp, string name,
        double? value, string unit, string? label = null, string state = "ok") =>
        metrics[name] = new MetricValue(name, value, unit, label, value.HasValue ? state : state == "ok" ? "unavailable" : state, timestamp);
    public void Dispose() => slow.Dispose();
    record ProcessCounter(long Created, long Cpu, ulong? Read, ulong? Write, long Time);
    record NetworkCounter(long Read, long Write, long Time);
}

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Zhulong.Core;

namespace Zhulong.Runtime.Collectors;

// 慢源各自限时；不可中断的卷查询最多保留一个在途任务，不累积线程。
internal sealed class SlowMetrics(RuntimeSettings settings) : IDisposable
{
    static readonly string[] GpuKeys = ["gpu_temp", "gpu_util", "gpu_mem_used", "gpu_mem_total"];
    static readonly string[] DiskKeys = ["io_read_mb_s", "io_write_mb_s", "disk_total_gb", "disk_free_gb", "disk_used_percent", "disk_temp_c"];
    static readonly string[] PingKeys = ["ping_gw_ms", "ping_net_ms", "ping_jitter_ms"];
    readonly object gate = new();
    readonly SemaphoreSlim running = new(1, 1);
    readonly CancellationTokenSource stopping = new();
    readonly Dictionary<string, MetricValue> values = new();
    readonly Dictionary<string, long> refreshedAt = new();
    int disposed;
    readonly Queue<double> pings = new();
    readonly Dictionary<int, DiskCounter> previousDisks = new();
    Task<Dictionary<string, MetricValue>>? diskTask;
    long? missingGpuAt;

    public Dictionary<string, MetricValue> Snapshot(DateTimeOffset now)
    {
        lock (gate)
        {
            var snapshot = new Dictionary<string, MetricValue>(values);
            foreach (var key in GpuKeys.Concat(DiskKeys).Concat(PingKeys))
                snapshot.TryAdd(key, new MetricValue(key, null, Unit(key), State: "not_sampled"));
            foreach (var (key, value) in snapshot.ToArray())
                if (value.Value.HasValue && value.SampledAt is { } time && refreshedAt.TryGetValue(key, out var refreshed) && Stopwatch.GetElapsedTime(refreshed).TotalSeconds > 180)
                    snapshot[key] = value with { Value = null, State = "stale" };
            return snapshot;
        }
    }
    public async Task CollectAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopping.Token);
        var token = linked.Token;
        if (!await running.WaitAsync(0, token)) return;
        try
        {
            // 60秒节拍只由宿主持有；此处仅防重叠，不能按启动时刻再次限频。
            await Task.WhenAll(GpuAsync(token), PingAsync(token), DisksAsync(token));
        }
        finally { running.Release(); }
    }
    void Publish(Dictionary<string, MetricValue> update, bool replaceDiskTemperatures = false)
    {
        lock (gate)
        {
            if (replaceDiskTemperatures)
                foreach (var key in values.Keys.Where(key => key.StartsWith("disk_", StringComparison.Ordinal) &&
                    key.EndsWith("_temp_c", StringComparison.Ordinal) && !update.ContainsKey(key)).ToArray())
                {
                    values.Remove(key);
                    refreshedAt.Remove(key);
                }
            foreach (var pair in update) { values[pair.Key] = pair.Value; refreshedAt[pair.Key] = Stopwatch.GetTimestamp(); }
        }
    }
    static string Unit(string key) => key.Contains("mb_s") ? "MB/s" : key.Contains("percent") || key == "gpu_util" ? "%" :
        key.EndsWith("_ms") ? "ms" : key == "gpu_temp" || key.EndsWith("_temp_c") ? "°C" : "GB";
    static Dictionary<string, MetricValue> Unknown(IEnumerable<string> keys, string state, string? label = null)
    {
        var now = DateTimeOffset.UtcNow;
        return keys.ToDictionary(key => key, key => new MetricValue(key, null, Unit(key), label, state, now));
    }

    async Task GpuAsync(CancellationToken token)
    {
        if (missingGpuAt is { } missing && Stopwatch.GetElapsedTime(missing).TotalMinutes < 10)
        { Publish(Unknown(GpuKeys, "not_installed_cached", "nvidia-smi")); return; }
        using var process = new Process { StartInfo = new ProcessStartInfo("nvidia-smi")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        process.StartInfo.ArgumentList.Add("--query-gpu=temperature.gpu,utilization.gpu,memory.used,memory.total");
        process.StartInfo.ArgumentList.Add("--format=csv,noheader,nounits");
        var started = false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            started = process.Start();
            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var output = await outputTask;
            await errorTask;
            if (process.ExitCode != 0) { Publish(Unknown(GpuKeys, "unavailable", "nvidia-smi")); return; }
            var fields = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Split(',') ?? [];
            var update = Unknown(GpuKeys, "unavailable", "NVIDIA首张显卡");
            for (var i = 0; i < Math.Min(fields.Length, 4); i++)
                if (double.TryParse(fields[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value))
                    SystemMonitor.Put(update, DateTimeOffset.UtcNow, GpuKeys[i], i >= 2 ? value / 1024 : value, Unit(GpuKeys[i]), "NVIDIA首张显卡");
            Publish(update);
        }
        catch (Win32Exception error)
        {
            if (error.NativeErrorCode == 2) missingGpuAt = Stopwatch.GetTimestamp();
            Publish(Unknown(GpuKeys, error.NativeErrorCode == 2 ? "not_installed" : "unavailable", "nvidia-smi"));
        }
        catch (OperationCanceledException)
        {
            if (started)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (Exception error) when (error is Win32Exception or InvalidOperationException) { }
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1)); }
                catch (TimeoutException) { }
            }
            Publish(Unknown(GpuKeys, token.IsCancellationRequested ? "cancelled" : "timeout", "nvidia-smi"));
            token.ThrowIfCancellationRequested();
        }
        catch (IOException) { Publish(Unknown(GpuKeys, "unavailable", "nvidia-smi")); }
    }

    async Task PingAsync(CancellationToken token)
    {
        if (!settings.PingEnabled) { Publish(Unknown(PingKeys, "disabled")); return; }
        var gateway = Gateway(settings.PingHost);
        var tasks = new[] { ProbeAsync(gateway, token), ProbeAsync(settings.PingHost, token) };
        var results = await Task.WhenAll(tasks);
        var now = DateTimeOffset.UtcNow;
        var update = Unknown(PingKeys, "unavailable");
        SystemMonitor.Put(update, now, "ping_gw_ms", results[0], "ms", gateway, gateway is null ? "no_gateway" : "ok");
        SystemMonitor.Put(update, now, "ping_net_ms", results[1], "ms", settings.PingHost);
        if (results[1] is { } latency)
        {
            pings.Enqueue(latency);
            while (pings.Count > 10) pings.Dequeue();
            var mean = pings.Average();
            var jitter = pings.Count >= 3 ? Math.Sqrt(pings.Average(value => Math.Pow(value - mean, 2))) : (double?)null;
            SystemMonitor.Put(update, now, "ping_jitter_ms", jitter, "ms", settings.PingHost, jitter.HasValue ? "ok" : "warming");
        }
        Publish(update);
    }
    static async Task<double?> ProbeAsync(string? host, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(host)) return null;
        using var ping = new Ping();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            var result = await ping.SendPingAsync(host, TimeSpan.FromMilliseconds(800), new byte[32], null, timeout.Token);
            return result.Status == IPStatus.Success ? result.RoundtripTime : null;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
        catch (Exception error) when (error is PingException or TimeoutException or ArgumentException) { return null; }
    }
    static string? Gateway(string host) => NativeCounters.Gateway(host);

    async Task DisksAsync(CancellationToken token)
    {
        if (diskTask is null || diskTask.IsCompleted) diskTask = Task.Run(() => ReadDisks(token), token);
        try { Publish(await diskTask.WaitAsync(TimeSpan.FromSeconds(3), token), replaceDiskTemperatures: true); }
        catch (TimeoutException) { Publish(Unknown(DiskKeys, "timeout"), replaceDiskTemperatures: true); }
        catch (IOException) { Publish(Unknown(DiskKeys, "unavailable"), replaceDiskTemperatures: true); }
        catch (UnauthorizedAccessException) { Publish(Unknown(DiskKeys, "unavailable"), replaceDiskTemperatures: true); }
    }
    Dictionary<string, MetricValue> ReadDisks(CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        var ticks = Stopwatch.GetTimestamp();
        var update = Unknown(DiskKeys, "unavailable");
        double read = 0, write = 0, busiestRate = -1;
        string? busiest = null;
        var available = false;
        var primed = false;
        short? hottest = null;
        string? hottestDrive = null;
        var hottestSampledAt = now;
        for (var disk = 0; disk < 32; disk++)
        {
            token.ThrowIfCancellationRequested();
            var counters = NativeCounters.Disk(disk);
            var (present, temperature) = NativeDiskTemperature.Read(disk);
            if (present || counters.HasValue)
            {
                var name = "PhysicalDrive" + disk;
                var sampledAt = DateTimeOffset.UtcNow;
                SystemMonitor.Put(update, sampledAt, $"disk_{disk}_temp_c", temperature, "°C", name);
                if (temperature.HasValue && (!hottest.HasValue || temperature.Value > hottest.Value))
                {
                    hottest = temperature;
                    hottestDrive = name;
                    hottestSampledAt = sampledAt;
                }
            }
            if (counters is not { } current) { previousDisks.Remove(disk); continue; }
            available = true;
            if (previousDisks.TryGetValue(disk, out var previous))
            {
                var seconds = Stopwatch.GetElapsedTime(previous.Time, ticks).TotalSeconds;
                if (seconds > 0 && current.BytesRead >= previous.Read && current.BytesWritten >= previous.Write)
                {
                    var diskRead = (current.BytesRead - previous.Read) / NativeCounters.Mb / seconds;
                    var diskWrite = (current.BytesWritten - previous.Write) / NativeCounters.Mb / seconds;
                    read += diskRead; write += diskWrite; primed = true;
                    if (diskRead + diskWrite > busiestRate) { busiestRate = diskRead + diskWrite; busiest = "PhysicalDrive" + disk; }
                }
            }
            previousDisks[disk] = new(current.BytesRead, current.BytesWritten, ticks);
        }
        SystemMonitor.Put(update, now, "io_read_mb_s", primed ? read : null, "MB/s", busiest, primed ? "ok" : available ? "warming" : "unavailable");
        SystemMonitor.Put(update, now, "io_write_mb_s", primed ? write : null, "MB/s", busiest, primed ? "ok" : available ? "warming" : "unavailable");
        SystemMonitor.Put(update, hottestSampledAt, "disk_temp_c", hottest, "°C", hottestDrive);
        double total = 0, free = 0, maximum = -1;
        string? fullest = null;
        foreach (var drive in DriveInfo.GetDrives())
        {
            token.ThrowIfCancellationRequested();
            if (drive.DriveType != DriveType.Fixed) continue;
            try
            {
                if (!drive.IsReady || drive.TotalSize <= 0) continue;
                var size = drive.TotalSize / NativeCounters.Gb;
                var remaining = drive.TotalFreeSpace / NativeCounters.Gb;
                total += size; free += remaining;
                var used = (size - remaining) * 100 / size;
                var prefix = "disk_" + drive.Name[0];
                SystemMonitor.Put(update, now, prefix + "_total_gb", size, "GB", drive.Name);
                SystemMonitor.Put(update, now, prefix + "_free_gb", remaining, "GB", drive.Name);
                if (used > maximum) { maximum = used; fullest = drive.Name; }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        SystemMonitor.Put(update, now, "disk_total_gb", total > 0 ? total : null, "GB", "固定本地卷合计");
        SystemMonitor.Put(update, now, "disk_free_gb", total > 0 ? free : null, "GB", "固定本地卷合计");
        SystemMonitor.Put(update, now, "disk_used_percent", maximum >= 0 ? maximum : null, "%", fullest);
        return update;
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stopping.Cancel(); stopping.Dispose();
    }
    record DiskCounter(long Read, long Write, long Time);
}

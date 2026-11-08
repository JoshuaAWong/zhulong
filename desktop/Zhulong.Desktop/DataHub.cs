using System.Text.Json;
using Zhulong.P0.AI;

namespace Zhulong.Desktop;

// 系统采样和AI读取分别调度；面板查询只消费已提交结果。
internal sealed class DataHub : IDisposable
{
    readonly object monitorGate = new(), usageGate = new();
    readonly SystemMonitor monitor = new();
    readonly MonitorStore history;
    readonly UsageStore usage;
    readonly CodexScanner scanner;
    readonly SemaphoreSlim refreshing = new(1, 1);
    readonly CancellationTokenSource stopped = new();
    Task? monitorLoop, usageLoop;
    SystemSnapshot? latest;
    JsonElement? usageReport;
    IEnumerator<string>? sources;
    string? activeSource;
    public event Action<SystemSnapshot>? Sampled;
    public string? UsageError { get; private set; }

    public DataHub(string directory)
    {
        Directory.CreateDirectory(directory);
        history = new MonitorStore(Path.Combine(directory, "monitor.db"));
        usage = new UsageStore(Path.Combine(directory, "ai.db"));
        scanner = new CodexScanner(usage);
    }

    public void Start()
    {
        monitorLoop = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
            do
            {
                var value = monitor.Collect();
                lock (monitorGate) history.Save(value);
                Volatile.Write(ref latest, value);
                Sampled?.Invoke(value);
            } while (await timer.WaitForNextTickAsync(stopped.Token));
        }, stopped.Token);
        usageLoop = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
            do { await RefreshUsageAsync(); }
            while (await timer.WaitForNextTickAsync(stopped.Token));
        }, stopped.Token);
    }

    public async Task<object?> QueryAsync(string api, string? metric = null, int hours = 24)
    {
        switch (api)
        {
            case "snapshot": return Volatile.Read(ref latest);
            case "history":
                if (metric is not ("commit" or "physical" or "cpu") || hours is not (24 or 168 or 720))
                    throw new ArgumentException("不支持的历史查询。");
                return await Task.Run(() => { lock (monitorGate) return new { points = history.History(metric, hours) }; });
            case "events":
                return await Task.Run(() => { lock (monitorGate) return new { events = history.Events() }; });
            case "refresh-usage":
                await RefreshUsageAsync();
                goto case "usage";
            case "usage":
                lock (usageGate) return new { report = usageReport, error = UsageError, scanning = refreshing.CurrentCount == 0 };
            default: throw new ArgumentException("不支持的面板请求。");
        }
    }

    async Task RefreshUsageAsync()
    {
        if (!await refreshing.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            await Task.Run(() =>
            {
                var home = Environment.GetEnvironmentVariable("CODEX_HOME") ??
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
                var state = Path.Combine(home, "state_5.sqlite");
                if (File.Exists(state)) CodexSessionSnapshots.ReadInto(usage, state);
                long bytes = 0;
                while (!stopped.IsCancellationRequested && bytes <= 12 * 1024 * 1024)
                {
                    if (activeSource is null)
                    {
                        if (sources is null)
                        {
                            var directory = Path.Combine(home, "sessions");
                            if (!Directory.Exists(directory)) break;
                            sources = Directory.EnumerateFiles(directory, "*.jsonl", SearchOption.AllDirectories)
                                .OrderDescending(StringComparer.Ordinal).GetEnumerator();
                        }
                        if (!sources.MoveNext()) { sources.Dispose(); sources = null; break; }
                        activeSource = sources.Current;
                    }
                    var result = scanner.ScanFile(activeSource);
                    bytes += result.BytesRead;
                    if (!result.HasMore) activeSource = null;
                }
                using var report = JsonDocument.Parse(usage.ReportJson(DateTimeOffset.UtcNow));
                lock (usageGate) { usageReport = report.RootElement.Clone(); UsageError = null; }
            }, stopped.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            // 错误保留上次快照及可见状态，不把失败发布成零。
            lock (usageGate) UsageError = error.Message;
            activeSource = null;
        }
        finally { refreshing.Release(); }
    }

    public async Task StopAsync()
    {
        stopped.Cancel();
        foreach (var loop in new[] { monitorLoop, usageLoop })
            if (loop is not null)
                try { await loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        await refreshing.WaitAsync().ConfigureAwait(false);
        refreshing.Release();
    }

    public void Dispose()
    {
        sources?.Dispose();
        history.Dispose();
        usage.Dispose();
        stopped.Dispose();
        refreshing.Dispose();
    }
}

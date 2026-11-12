using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Zhulong.Runtime.Providers.AI;

// 调度归宿主所有；本服务只执行有界工作，不创建计时器或独立常驻线程。
public sealed class AiUsageService : IAsyncDisposable, IDisposable
{
    readonly UsageStore store;
    readonly CodexScanner scanner;
    readonly CodexStateObserver stateObserver = new();
    readonly string home;
    readonly SemaphoreSlim refreshing = new(1, 1);
    readonly SemaphoreSlim refreshingAccount = new(1, 1);
    readonly CancellationTokenSource stopped = new();
    readonly object stateGate = new();
    readonly Func<string, CancellationToken, Task<CodexAccountReading>> readAccount;
    static readonly TimeSpan QuotaFreshFor = TimeSpan.FromMinutes(15);
    IncrementalSources? sources;
    string? activeSource;
    string? completedStateSignature;
    bool metadataPending;
    string? cachedReport;
    bool reportDirty = true;
    string? error;
    string state = "not_observed";
    DateTimeOffset? lastSuccess;
    long bytesRead;
    int visitedEntries;
    int scanning;
    int checkingAccount;
    CodexAccountReading? accountAttempt;
    AccountQuotaReading? accountQuota;
    AccountUsageReading? accountUsage;
    DateTimeOffset? accountAttemptAt;
    bool disposed;

    public AiUsageService(string databasePath, string? codexHome = null)
        : this(databasePath, codexHome, CodexAccountClient.ReadAsync) { }

    internal AiUsageService(string databasePath, string? codexHome,
        Func<string, CancellationToken, Task<CodexAccountReading>> readAccount)
    {
        var path = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        home = Path.GetFullPath(codexHome ?? Environment.GetEnvironmentVariable("CODEX_HOME") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"));
        store = new UsageStore(path);
        scanner = new CodexScanner(store);
        this.readAccount = readAccount;
    }

    public async Task<string> RefreshAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stopped.Token, cancellationToken);
        if (!await refreshing.WaitAsync(0, linked.Token).ConfigureAwait(false)) return Query();
        Volatile.Write(ref scanning, 1);
        try
        {
            await Task.Run(() => RefreshCore(linked.Token), linked.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsSourceError(exception))
        {
            lock (stateGate) { error = exception.Message; state = "error"; }
        }
        finally
        {
            Volatile.Write(ref scanning, 0);
            refreshing.Release();
        }
        return Query();
    }

    void RefreshCore(CancellationToken token)
    {
        const int batchBytes = 4 * 1024 * 1024;
        const long budget = 16 * 1024 * 1024;
        long read = 0;
        var visited = 0;
        var files = 0;
        var changed = false;
        string? failure = null;
        var observed = false;
        var statePath = Path.Combine(home, "state_5.sqlite");
        try
        {
            if (File.Exists(statePath))
            {
                observed = true;
                var signature = StateSignature(statePath) + "|" + stateObserver.Observe(statePath);
                if (metadataPending || signature != completedStateSignature)
                {
                    var batch = CodexSessionSnapshots.ReadBatchInto(store, statePath, 256, token);
                    metadataPending = batch.HasMore;
                    if (!metadataPending) completedStateSignature = signature;
                    changed |= batch.Rows > 0;
                    reportDirty |= batch.Rows > 0;
                }
            }
            else stateObserver.Dispose();
        }
        catch (Exception exception) when (IsSourceError(exception)) { failure = exception.Message; }

        while (read <= budget - batchBytes && files < 64 && visited < 256)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (activeSource is null)
                {
                    sources ??= new IncrementalSources([Path.Combine(home, "sessions"), Path.Combine(home, "archived_sessions")]);
                    activeSource = sources.Next(ref visited, 256, token, out var finished);
                    if (finished) { sources.Dispose(); sources = null; break; }
                    if (activeSource is null) break;
                }
                var result = scanner.ScanFile(activeSource, batchBytes, token);
                observed = true;
                files++;
                read += result.BytesRead;
                changed |= result.BytesRead > 0;
                reportDirty |= result.BytesRead > 0;
                if (!result.HasMore) activeSource = null;
            }
            catch (Exception exception) when (IsSourceError(exception))
            {
                // 出错来源不保留为当前项，下一轮从目录游标的后续来源继续。
                failure ??= exception.Message;
                activeSource = null;
                break;
            }
        }
        token.ThrowIfCancellationRequested();
        string? report = null;
        reportDirty |= changed;
        if (reportDirty)
        {
            report = store.ReportJson(DateTimeOffset.UtcNow);
            reportDirty = false;
        }
        lock (stateGate)
        {
            if (report is not null) cachedReport = report;
            error = failure;
            state = failure is not null ? "error" : observed ? "local_sources" : "not_observed";
            if (failure is null && observed) lastSuccess = DateTimeOffset.UtcNow;
            bytesRead = read;
            visitedEntries = visited;
        }
    }

    public Task<string> ReportAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Query());
    }

    // 账户读取由用户显式触发，与每分钟本地扫描独立。
    public async Task<string> RefreshAccountAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stopped.Token, cancellationToken);
        if (!await refreshingAccount.WaitAsync(0, linked.Token).ConfigureAwait(false)) return Query();
        Volatile.Write(ref checkingAccount, 1);
        try
        {
            var result = await readAccount(home, linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            lock (stateGate)
            {
                var signedOut = result.QuotaStatus is "not_signed_in" or "auth_unsupported";
                if (signedOut || result.AuthMode == "chatgpt")
                {
                    // 不保留账户标识。同一次已认证读取只采用同次返回的数据，避免跨账户混用。
                    accountQuota = null;
                    accountUsage = null;
                }
                accountQuota = result.Quota ?? accountQuota;
                accountUsage = result.Usage ?? accountUsage;
                // 仅保留最近尝试状态，账户聚合快照由各自字段保存。
                accountAttempt = result with
                {
                    AuthMode = result.AuthMode ?? (signedOut ? null : accountAttempt?.AuthMode),
                    PlanType = result.PlanType ?? (signedOut ? null : accountAttempt?.PlanType),
                    Quota = null, Usage = null
                };
                accountAttemptAt = DateTimeOffset.UtcNow;
            }
        }
        finally
        {
            Volatile.Write(ref checkingAccount, 0);
            refreshingAccount.Release();
        }
        return Query();
    }

    // 查询只使用已提交缓存；额度倒计时只做日期运算，不重新扫描来源或聚合全库。
    public string Query()
    {
        string? report;
        string? lastError;
        string sourceState;
        DateTimeOffset? success;
        long bytes;
        int entries;
        CodexAccountReading? attempt;
        AccountQuotaReading? quotaReading;
        AccountUsageReading? usageReading;
        DateTimeOffset? attemptAt;
        lock (stateGate)
        {
            report = cachedReport;
            lastError = error;
            sourceState = state;
            success = lastSuccess;
            bytes = bytesRead;
            entries = visitedEntries;
            attempt = accountAttempt;
            quotaReading = accountQuota;
            usageReading = accountUsage;
            attemptAt = accountAttemptAt;
        }
        var now = DateTimeOffset.UtcNow;
        var data = report is null ? null : JsonNode.Parse(report);
        if (sourceState == "not_observed" && data?["source_count"]?.GetValue<long>() == 0 &&
            data["metadata_session_count"]?.GetValue<long>() == 0) data = null;
        if (data?["quota"] is JsonArray quotas)
            foreach (var quota in quotas.OfType<JsonObject>())
            {
                var stale = true;
                if (DateTimeOffset.TryParse((string?)quota["observed_at"], out var observed))
                {
                    quota["snapshot_age_seconds"] = Math.Max(0, (now - observed).TotalSeconds);
                    stale = now - observed > QuotaFreshFor;
                }
                quota["state"] = stale ? "stale" : "snapshot";
                foreach (var name in new[] { "primary", "secondary" })
                    if (quota[name] is JsonObject window)
                    {
                        window["status"] = stale ? "stale" : "snapshot";
                        if (DateTimeOffset.TryParse((string?)window["resets_at"], out var reset))
                        {
                            var remaining = Math.Max(0, Math.Ceiling((reset - now).TotalSeconds));
                            window["remaining_seconds"] = remaining;
                            if (remaining == 0) window["status"] = "awaiting_refresh";
                        }
                        if (stale || (string?)window["status"] == "awaiting_refresh") window["remaining_percent"] = null;
                    }
            }
        var quotaStatus = AccountState(quotaReading?.FetchedAt, attempt?.QuotaStatus, now);
        var usageStatus = AccountState(usageReading?.FetchedAt, attempt?.UsageStatus, now);
        var refreshingAccountNow = Volatile.Read(ref checkingAccount) != 0;
        var windows = quotaReading?.Buckets.Select(bucket => new
        {
            limit_id = bucket.LimitId, limit_name = bucket.LimitName,
            primary = QuotaView(bucket.Primary, quotaStatus, now),
            secondary = QuotaView(bucket.Secondary, quotaStatus, now)
        }).ToArray() ?? [];
        if (quotaStatus == "fresh" && windows.Length > 0 && windows.All(window =>
            (window.primary is null || (string?)window.primary["status"] == "awaiting_refresh") &&
            (window.secondary is null || (string?)window.secondary["status"] == "awaiting_refresh")))
            quotaStatus = "awaiting_refresh";
        return JsonSerializer.Serialize(new
        {
            report = data, error = lastError, scanning = Volatile.Read(ref scanning) != 0,
            last_success_at = success, source_state = sourceState, bytes_read = bytes,
            discovery_entries = entries, metadata_pending = metadataPending,
            account_quota = new
            {
                state = quotaStatus, source = "codex_app_server", auth_mode = attempt?.AuthMode, plan_type = attempt?.PlanType,
                fetched_at = quotaReading?.FetchedAt, last_attempt_at = attemptAt, attempt_status = attempt?.QuotaStatus,
                refreshing = refreshingAccountNow, windows
            },
            account_usage = new
            {
                state = usageStatus, source = "codex_app_server", fetched_at = usageReading?.FetchedAt,
                last_attempt_at = attemptAt, attempt_status = attempt?.UsageStatus, refreshing = refreshingAccountNow,
                lifetime_tokens = usageReading?.LifetimeTokens,
                daily_usage_buckets = usageReading?.Days?.Select(day => new { start_date = day.StartDate, tokens = day.Tokens })
            },
            providers = new[] { new { tool = "codex", provider = "openai", state = sourceState },
                new { tool = "kimi", provider = "moonshot", state = "not_implemented" },
                new { tool = "zcode", provider = "model_dependent", state = "not_implemented" } }
        });
    }

    static string AccountState(DateTimeOffset? fetchedAt, string? attemptStatus, DateTimeOffset now) =>
        fetchedAt is null ? attemptStatus ?? "not_checked"
            : attemptStatus == "ok" && now - fetchedAt.Value <= QuotaFreshFor ? "fresh" : "stale";

    static JsonObject? QuotaView(QuotaWindow? window, string quotaStatus, DateTimeOffset now)
    {
        if (window is null) return null;
        var seconds = window.ResetsAt is { } reset ? Math.Max(0, Math.Ceiling((reset - now).TotalSeconds)) : (double?)null;
        var status = seconds == 0 ? "awaiting_refresh" : quotaStatus;
        return new JsonObject
        {
            ["used_percent"] = window.UsedPercent,
            ["remaining_percent"] = status == "fresh" && window.UsedPercent is { } used ? Math.Clamp(100 - used, 0, 100) : null,
            ["window_minutes"] = window.WindowMinutes,
            ["resets_at"] = window.ResetsAt?.ToString("O"),
            ["remaining_seconds"] = seconds,
            ["status"] = status
        };
    }

    static string StateSignature(string path)
    {
        string Signature(string file)
        {
            var info = new FileInfo(file);
            return info.Exists ? $"{info.Length}:{info.LastWriteTimeUtc.Ticks}:{info.CreationTimeUtc.Ticks}" : "missing";
        }
        return Signature(path) + "|" + Signature(path + "-wal");
    }

    static bool IsSourceError(Exception exception) => exception is InvalidDataException or IOException or
        UnauthorizedAccessException or SqliteException or JsonException or ArgumentException;

    public async Task StopAsync()
    {
        stopped.Cancel();
        await refreshingAccount.WaitAsync().ConfigureAwait(false);
        refreshingAccount.Release();
        await refreshing.WaitAsync().ConfigureAwait(false);
        try
        {
            try { sources?.Dispose(); sources = null; activeSource = null; }
            finally { stateObserver.Dispose(); }
        }
        finally { refreshing.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        await StopAsync().ConfigureAwait(false);
        store.Dispose();
        refreshing.Dispose();
        refreshingAccount.Dispose();
        stopped.Dispose();
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}

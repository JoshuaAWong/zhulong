using System.ComponentModel;
using System.Text.Json;

namespace Zhulong.Runtime.Providers.AI;

internal sealed class AdditionalAiUsage : IAsyncDisposable
{
    const string Primary = "kimi-primary", Secondary = "kimi-secondary", Glm = "glm", DeepSeek = "deepseek";
    readonly string directory, primaryHome, secondaryHome;
    readonly LocalUsage kimi, secondKimi, glm;
    readonly Func<string, CancellationToken, Task<ProviderAccountReading>> readKimi, readDeepSeek;
    readonly Dictionary<string, AccountSnapshot> accounts = new(StringComparer.Ordinal);
    readonly SemaphoreSlim localGate = new(1), accountGate = new(1);
    readonly CancellationTokenSource stopping = new();
    readonly object gate = new();
    string? refreshingAccount;
    readonly bool sharedKimiHome;

    internal AdditionalAiUsage(string directory, string? kimiHome = null, string? zcodePath = null,
        Func<string, CancellationToken, Task<ProviderAccountReading>>? readKimi = null,
        Func<string, CancellationToken, Task<ProviderAccountReading>>? readDeepSeek = null)
    {
        this.directory = directory;
        this.readKimi = readKimi ?? KimiAccountClient.ReadAsync;
        this.readDeepSeek = readDeepSeek ?? DeepSeekAccountClient.ReadAsync;
        primaryHome = kimiHome ?? AiAccountProfiles.DefaultKimiHome;
        secondaryHome = AiAccountProfiles.SecondaryKimiHome(directory);
        sharedKimiHome = string.Equals(Path.GetFullPath(primaryHome).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(secondaryHome).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        kimi = KimiLocal(primaryHome, "kimi-usage.db");
        secondKimi = KimiLocal(secondaryHome, "kimi-secondary-usage.db");
        zcodePath ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".zcode", "cli", "db", "db.sqlite");
        var source = new ZCodeScanner(zcodePath);
        glm = new(Path.Combine(directory, "glm-usage.db"), "zcode", "glm",
            () => File.Exists(zcodePath), (store, token) => source.ScanBatch(store, 4096, token));
    }

    LocalUsage KimiLocal(string home, string database)
    {
        var source = new KimiSessionSource(home);
        return new(Path.Combine(directory, database), "kimi", "moonshot",
            () => Directory.Exists(Path.Combine(home, "sessions")), source.ScanBatch, source);
    }

    internal async Task RefreshLocalAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token, cancellationToken);
        var token = linked.Token;
        if (!await localGate.WaitAsync(0, token).ConfigureAwait(false)) return;
        try
        {
            await Task.Run(() =>
            {
                kimi.Refresh(token);
                if (!sharedKimiHome) secondKimi.Refresh(token);
                glm.Refresh(token);
            }, token).ConfigureAwait(false);
        }
        finally { localGate.Release(); }
    }

    internal async Task RefreshAccountAsync(string id, CancellationToken cancellationToken)
    {
        if (id is not (Primary or Secondary or Glm or DeepSeek)) throw new ArgumentException("未知的 AI 账户。");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token, cancellationToken);
        var token = linked.Token;
        if (!await accountGate.WaitAsync(0, token).ConfigureAwait(false)) return;
        lock (gate) refreshingAccount = id;
        try
        {
            ProviderAccountReading result;
            AiAccountPreferences? preferences = null;
            string? walletKeyVersion = null;
            try
            {
                if (id == DeepSeek)
                {
                    var key = AiAccountProfiles.ReadDeepSeekKey(directory, out walletKeyVersion);
                    result = key is null ? new("not_configured", null, null, []) : await readDeepSeek(key, token).ConfigureAwait(false);
                }
                else if (id == Glm)
                {
                    preferences = AiAccountProfiles.Load(directory);
                    var key = AiAccountProfiles.ReadGlmKey(directory);
                    result = key is null ? new("not_configured", null, null, [])
                        : await ZCodeAccountClient.ReadAsync(key, preferences.GlmDomestic, token).ConfigureAwait(false);
                }
                else if (id == Secondary && sharedKimiHome) result = new("duplicate_profile", null, null, []);
                else
                {
                    var home = id == Primary ? primaryHome : secondaryHome;
                    result = Directory.Exists(home) ? await readKimi(home, token).ConfigureAwait(false)
                        : new("not_signed_in", null, null, []);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or Win32Exception)
            { result = new("unavailable", null, null, []); }
            token.ThrowIfCancellationRequested();
            lock (gate)
            {
                var now = DateTimeOffset.UtcNow;
                var snapshot = new AccountSnapshot(result, now, preferences?.GlmKeyRevision, preferences?.GlmDomestic, walletKeyVersion);
                if (result.State is ("unavailable" or "timeout") && accounts.TryGetValue(id, out var old) &&
                    (id == Glm && old.Revision == snapshot.Revision && old.Domestic == snapshot.Domestic && old.Reading.Windows.Count > 0 ||
                    id == DeepSeek && walletKeyVersion is not null && old.WalletKeyVersion == walletKeyVersion && old.Reading.Wallet is not null))
                    snapshot = old with { Reading = old.Reading with { State = "stale" } };
                accounts[id] = snapshot;
            }
        }
        finally
        {
            lock (gate) refreshingAccount = null;
            accountGate.Release();
        }
    }

    internal object Report()
    {
        AiAccountPreferences preferences;
        try { preferences = AiAccountProfiles.Load(directory); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { preferences = new(); }
        string? walletKeyVersion = null, walletOverride = null;
        try { walletKeyVersion = AiAccountProfiles.DeepSeekKeyVersion(directory); }
        catch (Win32Exception) { walletOverride = "unavailable"; }
        if (walletKeyVersion is null) walletOverride ??= "not_configured";
        var now = DateTimeOffset.UtcNow;
        lock (gate)
        {
            var primary = accounts.GetValueOrDefault(Primary);
            var secondary = accounts.GetValueOrDefault(Secondary);
            var plan = accounts.GetValueOrDefault(Glm);
            if (plan is not null && (plan.Revision != preferences.GlmKeyRevision || plan.Domestic != preferences.GlmDomestic)) plan = null;
            var wallet = accounts.GetValueOrDefault(DeepSeek);
            if (wallet?.WalletKeyVersion != walletKeyVersion) wallet = null;
            var duplicate = primary?.Reading.Identity is { } identity && identity == secondary?.Reading.Identity;
            var (kimiState, kimiReport) = kimi.Snapshot;
            var (secondState, secondReport) = secondKimi.Snapshot;
            var (glmState, glmReport) = glm.Snapshot;
            return new[]
            {
                new { tool = "kimi", state = kimiState, report = kimiReport, accounts = new[]
                {
                    Account(Primary, preferences.KimiPrimary, "kimi_local_api", primary, now, null, null),
                    Account(Secondary, preferences.KimiSecondary, "kimi_local_api", secondary, now,
                        duplicate ? "duplicate_account" : sharedKimiHome ? "duplicate_profile" : null,
                        duplicate || sharedKimiHome ? null : secondReport, secondState)
                } },
                new { tool = "zcode", state = glmState, report = glmReport, accounts = new[]
                {
                    Account(Glm, preferences.Glm, "glm_official_plan", plan, now, null, null)
                } },
                new { tool = DeepSeek, state = AccountState(wallet, now, walletOverride), report = (JsonElement?)null, accounts = new[]
                {
                    Account(DeepSeek, "DeepSeek API 钱包", "deepseek_official_balance", wallet, now, walletOverride, null)
                } }
            };
        }
    }

    object Account(string id, string label, string source, AccountSnapshot? snapshot,
        DateTimeOffset now, string? overrideState, JsonElement? localReport, string? localState = null)
    {
        var reading = snapshot?.Reading;
        var state = AccountState(snapshot, now, overrideState);
        var wallet = overrideState is null ? reading?.Wallet : null;
        var windows = overrideState is null ? reading?.Windows.Select(window =>
        {
            var seconds = window.ResetsAt is { } reset ? Math.Max(0, Math.Ceiling((reset - now).TotalSeconds)) : (double?)null;
            var status = seconds == 0 ? "awaiting_refresh" : state == "ok" ? "fresh" : state;
            return new
            {
                name = window.Name, used_percent = window.UsedPercent,
                remaining_percent = status == "fresh" && window.UsedPercent is { } used ? Math.Clamp(100 - used, 0, 100) : (double?)null,
                resets_at = window.ResetsAt, remaining_seconds = seconds, window_minutes = window.WindowMinutes,
                total_units = window.TotalUnits, used_units = window.UsedUnits, unit = window.Unit, status
            };
        }).ToArray() : null;
        return new { id, label, state, source, plan_type = overrideState is null ? reading?.PlanType : null,
            fetched_at = overrideState is null && (reading?.Windows.Count > 0 || wallet is not null) ? snapshot?.Time : null,
            account_tokens = overrideState is null ? reading?.AccountTokens : null, windows,
            is_available = wallet?.IsAvailable,
            balances = wallet?.Balances.Select(balance => new { currency = balance.Currency, total_balance = balance.Total,
                granted_balance = balance.Granted, topped_up_balance = balance.ToppedUp }).ToArray() ?? [],
            local_report = localReport, local_state = localState, refreshing = refreshingAccount == id };
    }

    static string AccountState(AccountSnapshot? snapshot, DateTimeOffset now, string? overrideState)
    {
        var state = overrideState ?? snapshot?.Reading.State ?? "not_checked";
        return state == "ok" && snapshot is not null && now - snapshot.Time > TimeSpan.FromMinutes(15) ? "stale" : state;
    }

    public async ValueTask DisposeAsync()
    {
        stopping.Cancel();
        await localGate.WaitAsync().ConfigureAwait(false);
        await accountGate.WaitAsync().ConfigureAwait(false);
        kimi.Dispose();
        secondKimi.Dispose();
        glm.Dispose();
        localGate.Dispose();
        accountGate.Dispose();
        stopping.Dispose();
    }

    sealed record AccountSnapshot(ProviderAccountReading Reading, DateTimeOffset Time, Guid? Revision, bool? Domestic, string? WalletKeyVersion);

    sealed class LocalUsage(string database, string tool, string provider, Func<bool> exists,
        Func<UsageStore, CancellationToken, ScanResult> scan, IDisposable? source = null) : IDisposable
    {
        UsageStore? store;
        SnapshotData snapshot = new("not_observed", null);

        internal (string State, JsonElement? Report) Snapshot
        {
            get { var value = Volatile.Read(ref snapshot); return (value.State, value.Report); }
        }

        internal void Refresh(CancellationToken token)
        {
            try
            {
                if (store is null && !exists() && !File.Exists(database)) return;
                store ??= new(database);
                var current = Volatile.Read(ref snapshot);
                var available = exists();
                var result = available ? scan(store, token) : null;
                var report = current.Report;
                if (report is null || result?.EventsAdded > 0)
                {
                    using var document = JsonDocument.Parse(store.ReportJson(DateTimeOffset.UtcNow, tool, provider));
                    report = document.RootElement.Clone();
                }
                var state = !available ? "source_missing" : result?.HasMore == true ? "scanning" : "local_sources";
                Volatile.Write(ref snapshot, new(state, report));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException
                or JsonException or Microsoft.Data.Sqlite.SqliteException)
            {
                Volatile.Write(ref snapshot, Volatile.Read(ref snapshot) with { State = "partial" });
            }
        }

        public void Dispose() { source?.Dispose(); store?.Dispose(); }

        sealed record SnapshotData(string State, JsonElement? Report);
    }
}

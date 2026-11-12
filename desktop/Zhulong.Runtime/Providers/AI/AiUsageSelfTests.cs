using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Zhulong.Runtime.Providers.AI;

public static class AiUsageSelfTests
{
    public static int Run(string directory) => RunAsync(directory).GetAwaiter().GetResult();

    static async Task<int> TestAccountIsolationAsync(string directory)
    {
        var primary = Path.Combine(directory, "kimi-primary");
        var secondary = AiAccountProfiles.SecondaryKimiHome(directory);
        Directory.CreateDirectory(primary); Directory.CreateDirectory(secondary);
        var secondIdentity = "fixture-b";
        var reset = DateTimeOffset.UtcNow.AddHours(2);
        await using var source = new AdditionalAiUsage(directory, primary, Path.Combine(directory, "missing-zcode.db"), (home, _) =>
            Task.FromResult(new ProviderAccountReading("ok", home == primary ? "fixture-a" : secondIdentity, null,
                [new("limit5h", home == primary ? 11 : 47, reset)])));
        await source.RefreshAccountAsync("kimi-primary", CancellationToken.None);
        await source.RefreshAccountAsync("kimi-secondary", CancellationToken.None);
        var rows = JsonSerializer.SerializeToElement(source.Report())[0].GetProperty("accounts");
        if (rows[0].GetProperty("windows")[0].GetProperty("remaining_percent").GetDouble() != 89 ||
            rows[1].GetProperty("windows")[0].GetProperty("remaining_percent").GetDouble() != 53)
            throw new InvalidOperationException("两个 Kimi 目录的额度不得串号。");
        if (rows.GetRawText().Contains("fixture-a", StringComparison.Ordinal) || rows.GetRawText().Contains("fixture-b", StringComparison.Ordinal))
            throw new InvalidOperationException("账号身份不得发送至面板。");
        secondIdentity = "fixture-a";
        await source.RefreshAccountAsync("kimi-secondary", CancellationToken.None);
        rows = JsonSerializer.SerializeToElement(source.Report())[0].GetProperty("accounts");
        if (rows[1].GetProperty("state").GetString() != "duplicate_account" || rows[1].GetProperty("windows").ValueKind != JsonValueKind.Null)
            throw new InvalidOperationException("重复登录必须提示且不得重复展示额度。");
        return 3;
    }

    public static async Task<int> RunAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var assertions = 0;
        void Check(bool condition, string message)
        {
            assertions++;
            if (!condition) throw new InvalidOperationException(message);
        }
        var test = Path.GetFullPath(Path.Combine(directory, "ai-" + Guid.NewGuid().ToString("N")));
        var home = Path.Combine(test, "codex");
        var logs = Path.Combine(home, "sessions");
        var now = DateTimeOffset.UtcNow;
        Directory.CreateDirectory(logs);
        string Line(string type, object payload, int seconds = 0) => JsonSerializer.Serialize(new
        { timestamp = now.AddSeconds(seconds).ToString("O"), type, payload }) + "\n";
        var meta = Line("session_meta", new { id = "runtime-fixture" });
        object Usage(int input, int output) => new { input_tokens = input, output_tokens = output, total_tokens = input + output };
        string Count(int input, int output, int totalInput, int totalOutput, int seconds) => Line("event_msg", new
        {
            type = "token_count", info = new { last_token_usage = Usage(input, output), total_token_usage = Usage(totalInput, totalOutput) }
        }, seconds);
        var bad = Path.Combine(logs, "bad.jsonl");
        var normal = Path.Combine(logs, "normal.jsonl");
        File.WriteAllText(bad, meta + new string('x', 4 * 1024 * 1024 + 1024) + "\n", new UTF8Encoding(false));
        File.WriteAllText(normal, meta + Count(10, 2, 10, 2, 1), new UTF8Encoding(false));
        var target = Path.Combine(test, "ai.db");
        await using (var service = new AiUsageService(target, home))
        {
            using (var first = JsonDocument.Parse(await service.RefreshAsync()))
            {
                Check(!string.IsNullOrWhiteSpace(first.RootElement.GetProperty("error").GetString()), "超限来源须产生可见错误");
                Check(!first.RootElement.GetProperty("scanning").GetBoolean(), "异常后刷新锁必须释放");
                Check(first.RootElement.GetProperty("bytes_read").GetInt64() <= 16 * 1024 * 1024, "每轮来源读取应受预算约束");
            }
            using (var second = JsonDocument.Parse(await service.RefreshAsync()))
            {
                Check(second.RootElement.GetProperty("error").ValueKind == JsonValueKind.Null, "后续来源应恢复正常扫描");
                Check(second.RootElement.GetProperty("report").GetProperty("totals").GetProperty("total_tokens").GetInt64() == 12, "坏来源不能阻止正常请求计账");
                Check(second.RootElement.GetProperty("report").GetProperty("tool").GetString() == "codex", "工具与模型维度应分开");
            }
            using (var connection = Open(target))
            {
                using var cursor = connection.CreateCommand();
                cursor.CommandText = "SELECT offset FROM source_cursors WHERE path=$path";
                cursor.Parameters.AddWithValue("$path", bad);
                Check(Convert.ToInt64(cursor.ExecuteScalar()) == Encoding.UTF8.GetByteCount(meta), "超限行不能推进已提交游标");
            }
            File.Delete(bad);
            using (var unchanged = JsonDocument.Parse(await service.RefreshAsync()))
                Check(unchanged.RootElement.GetProperty("bytes_read").GetInt64() == 0, "无变化来源不得重读日志内容");
            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel();
                var rejected = false;
                try { await service.RefreshAsync(canceled.Token); } catch (OperationCanceledException) { rejected = true; }
                Check(rejected, "显式取消必须生效");
            }
            File.AppendAllText(normal, Count(10, 2, 20, 4, 2), new UTF8Encoding(false));
            using (var connection = Open(target)) Execute(connection,
                "CREATE TRIGGER fixture_abort BEFORE INSERT ON usage_records BEGIN SELECT RAISE(ABORT,'fixture failure'); END");
            using (var failure = JsonDocument.Parse(await service.RefreshAsync()))
                Check(failure.RootElement.GetProperty("error").ValueKind == JsonValueKind.String, "必须实际触发写入失败");
            using (var connection = Open(target))
            {
                Check(Scalar(connection, "SELECT SUM(total_tokens) FROM usage_records") == 12, "写入失败应回滚用量");
                Execute(connection, "DROP TRIGGER fixture_abort");
            }
            for (var i = 0; i < 3; i++) await service.RefreshAsync();
            using (var recovered = JsonDocument.Parse(await service.ReportAsync()))
                Check(recovered.RootElement.GetProperty("report").GetProperty("totals").GetProperty("total_tokens").GetInt64() == 24,
                    "异常和取消后重试不得漏计或重复");
            await service.StopAsync();
        }

        await using (var absent = new AiUsageService(Path.Combine(test, "absent.db"), Path.Combine(test, "missing")))
        using (var missing = JsonDocument.Parse(await absent.RefreshAsync()))
        {
            Check(missing.RootElement.GetProperty("source_state").GetString() == "not_observed", "来源缺失应为未观察");
            Check(missing.RootElement.GetProperty("report").ValueKind == JsonValueKind.Null, "来源缺失不能伪装成零用量");
        }

        var sourcePath = Path.Combine(test, "state.sqlite");
        using (var source = Open(sourcePath))
        using (var store = new UsageStore(Path.Combine(test, "snapshots.db")))
        {
            Execute(source, "PRAGMA journal_mode=WAL;CREATE TABLE threads(id TEXT PRIMARY KEY,tokens_used INTEGER NOT NULL,updated_at INTEGER NOT NULL);CREATE INDEX thread_updated ON threads(updated_at,id)");
            var epoch = now.ToUnixTimeSeconds();
            for (var i = 0; i < 400; i++) Execute(source,
                $"INSERT INTO threads VALUES('t{i:D3}',{i * 10},{epoch + i})");
            Check(File.Exists(sourcePath + "-wal"), "夹具必须保留活跃 WAL");
            var first = CodexSessionSnapshots.ReadBatchInto(store, sourcePath);
            Check(first.Rows == 256 && first.HasMore, "元数据读取须按256行分页");
            var second = CodexSessionSnapshots.ReadBatchInto(store, sourcePath);
            Check(second.Rows == 144 && !second.HasMore, "元数据分页应继续而非重头扫描");
            Check(Scalar(store.Connection, "SELECT COUNT(*) FROM session_snapshots") == 400, "分页元数据应完整保存");
            Check(Scalar(store.Connection, "SELECT total_tokens FROM session_snapshots WHERE session_id='t000'") == 0, "零累计快照必须原样保留");
            Execute(source, $"UPDATE threads SET tokens_used=999,updated_at={epoch + 400} WHERE id='t000'");
            var next = CodexSessionSnapshots.ReadBatchInto(store, sourcePath);
            Check(next.Rows <= 3, "后续元数据只读水位变化与重叠秒");
            Check(Scalar(store.Connection, "SELECT total_tokens FROM session_snapshots WHERE session_id='t000'") == 999, "快照应覆盖而非累计相加");
            Check(Scalar(store.Connection, "SELECT COUNT(*) FROM usage_records") == 0, "状态快照不能贡献请求用量");
            Check(Scalar(source, "SELECT COUNT(*) FROM threads") == 400, "来源表不得被修改");

            using var cancel = new CancellationTokenSource();
            Execute(store.Connection, "DELETE FROM metadata_sync;DELETE FROM session_snapshots");
            store.Connection.CreateFunction("fixture_cancel", () => { cancel.Cancel(); return 0; });
            Execute(store.Connection, "CREATE TRIGGER fixture_cancel_trigger BEFORE INSERT ON session_snapshots WHEN NEW.session_id='t003' BEGIN SELECT fixture_cancel(); END");
            var canceled = false;
            try { CodexSessionSnapshots.ReadBatchInto(store, sourcePath, 256, cancel.Token); }
            catch (OperationCanceledException) { canceled = true; }
            Check(canceled, "必须在元数据批次中途真实触发取消");
            Check(Scalar(store.Connection, "SELECT COUNT(*) FROM session_snapshots") == 0, "取消必须回滚已写快照");
            Check(Scalar(store.Connection, "SELECT COUNT(*) FROM metadata_sync") == 0, "取消必须回滚同步游标");
            Execute(store.Connection, "DROP TRIGGER fixture_cancel_trigger");
            Check(CodexSessionSnapshots.ReadBatchInto(store, sourcePath).Rows == 256, "取消后必须可从同一批次重试");
        }
        await CheckStableFileMetadataAsync(test, now, Check);
        await CheckAccountAsync(test, home, now, Check);
        assertions += await ZCodeSelfTests.RunAsync(directory);
        assertions += KimiUsageSelfTests.Run(Path.Combine(directory, "kimi-usage"));
        assertions += await TestAccountIsolationAsync(Path.Combine(directory, "profile-isolation"));
        await CheckDeepSeekWalletAsync(test, Check);
        return assertions;
    }

    static async Task CheckDeepSeekWalletAsync(string directory, Action<bool, string> check)
    {
        var data = Path.Combine(directory, "deepseek-wallet");
        Directory.CreateDirectory(data);
        const string key = "fixture-deepseek";
        const string body = """
            {"is_available":false,"balance_infos":[
              {"currency":"CNY","total_balance":"12.34","granted_balance":"0.00","topped_up_balance":"12.34"},
              {"currency":"USD","total_balance":-0.01,"topped_up_balance":"invalid"}]}
            """;
        using var http = new WalletHttp(body, key);
        using var client = new HttpClient(http);
        await using var source = new AdditionalAiUsage(data, Path.Combine(data, "kimi"), Path.Combine(data, "zcode.db"),
            readDeepSeek: (credential, token) => DeepSeekAccountClient.ReadAsync(client, credential, token));
        JsonElement Account() => JsonSerializer.SerializeToElement(source.Report()).EnumerateArray()
            .Single(provider => provider.GetProperty("tool").GetString() == "deepseek").GetProperty("accounts")[0];
        var token = CancellationToken.None;
        try
        {
            await source.RefreshAccountAsync("deepseek", token);
            check(Account().GetProperty("state").GetString() == "not_configured" && http.Calls == 0,
                "未配置 DeepSeek Key 时不能发送请求");

            AiAccountProfiles.SaveDeepSeekKey(data, key);
            check(AiAccountProfiles.HasDeepSeekKey(data) && !AiAccountProfiles.HasGlmKey(data) &&
                !File.Exists(Path.Combine(data, "ai-profiles.json")), "DeepSeek Key 必须独立存放在 Windows 凭据中");
            await source.RefreshAccountAsync("deepseek", token);
            var wallet = Account();
            var balances = wallet.GetProperty("balances");
            check(http.Calls == 1 && http.IsExpectedRequest && wallet.GetProperty("state").GetString() == "ok" &&
                wallet.GetProperty("fetched_at").GetDateTimeOffset() <= DateTimeOffset.UtcNow, "按需查询须调用固定的官方余额地址");
            check(!wallet.GetProperty("is_available").GetBoolean() && wallet.GetProperty("windows").GetArrayLength() == 0 &&
                balances.GetArrayLength() == 2 && balances[0].GetProperty("currency").GetString() == "CNY" &&
                balances[0].GetProperty("total_balance").GetDecimal() == 12.34m && balances[0].GetProperty("granted_balance").GetDecimal() == 0 &&
                balances[1].GetProperty("currency").GetString() == "USD" && balances[1].GetProperty("total_balance").GetDecimal() == -.01m &&
                balances[1].GetProperty("topped_up_balance").ValueKind == JsonValueKind.Null &&
                !wallet.GetRawText().Contains(key, StringComparison.Ordinal), "按币种保留余额、零值、未知值和可用状态，不产生套餐窗口或泄露凭据");

            http.Status = HttpStatusCode.ServiceUnavailable;
            await source.RefreshAccountAsync("deepseek", token);
            check(Account().GetProperty("state").GetString() == "stale" &&
                Account().GetProperty("fetched_at").GetDateTimeOffset() == wallet.GetProperty("fetched_at").GetDateTimeOffset(),
                "临时查询失败时显示同一密钥下的上次成功快照");
            AiAccountProfiles.SaveDeepSeekKey(data, "fixture-deepseek-new");
            check(Account().GetProperty("state").GetString() == "not_checked" && Account().GetProperty("balances").GetArrayLength() == 0,
                "更换密钥后隐藏之前的余额");
            http.Status = HttpStatusCode.Unauthorized;
            await source.RefreshAccountAsync("deepseek", token);
            check(Account().GetProperty("state").GetString() == "auth_rejected", "DeepSeek 认证失败需保留独立状态");
            AiAccountProfiles.SaveDeepSeekKey(data, null);
            check(!AiAccountProfiles.HasDeepSeekKey(data) && Account().GetProperty("state").GetString() == "not_configured",
                "移除密钥后回到未配置状态");
        }
        finally { AiAccountProfiles.SaveDeepSeekKey(data, null); }

    }

    // 捕获请求形状，不连接任何外部地址。
    sealed class WalletHttp(string body, string expectedKey) : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public int Calls { get; private set; }
        public bool IsExpectedRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            IsExpectedRequest = request.Method == HttpMethod.Get && request.RequestUri?.AbsoluteUri == "https://api.deepseek.com/user/balance" &&
                request.Headers.Authorization?.Scheme == "Bearer" && request.Headers.Authorization?.Parameter == expectedKey;
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(body) });
        }
    }

    static async Task CheckStableFileMetadataAsync(string test, DateTimeOffset now, Action<bool, string> check)
    {
        var home = Path.Combine(test, "stable-metadata");
        Directory.CreateDirectory(home);
        var path = Path.Combine(home, "state_5.sqlite");
        var target = Path.Combine(test, "stable-metadata-target.db");
        using var writer = Open(path);
        Execute(writer, "PRAGMA journal_mode=WAL;PRAGMA wal_autocheckpoint=0;CREATE TABLE threads(id TEXT PRIMARY KEY,tokens_used INTEGER NOT NULL,updated_at INTEGER NOT NULL);CREATE INDEX idx_threads_updated_at ON threads(updated_at DESC,id DESC)");
        Execute(writer, $"INSERT INTO threads VALUES('stable-counter',100,{now.ToUnixTimeSeconds()})");
        Execute(writer, "PRAGMA wal_checkpoint(RESTART);UPDATE threads SET tokens_used=101");
        await using var service = new AiUsageService(target, home);
        using (var initial = JsonDocument.Parse(await service.RefreshAsync()))
            check(initial.RootElement.GetProperty("report").GetProperty("metadata_sessions")[0].GetProperty("total_tokens").GetInt64() == 101, "元数据初次快照必须可用");
        using (var targetWriter = Open(target))
        {
            Execute(targetWriter, "CREATE TABLE fixture_meta_writes(count INTEGER);INSERT INTO fixture_meta_writes VALUES(0);CREATE TRIGGER fixture_meta_write AFTER UPDATE ON session_snapshots BEGIN UPDATE fixture_meta_writes SET count=count+1; END");
            await service.RefreshAsync();
            check(Scalar(targetWriter, "SELECT count FROM fixture_meta_writes") == 0, "没有外部提交时不能重导元数据");
        }
        var database = new FileInfo(path); var wal = new FileInfo(path + "-wal");
        var databaseLength = database.Length; var walLength = wal.Length;
        var databaseTime = database.LastWriteTimeUtc; var walTime = wal.LastWriteTimeUtc;
        using (var checkpoint = writer.CreateCommand())
        {
            checkpoint.CommandText = "PRAGMA wal_checkpoint(RESTART)";
            using var reader = checkpoint.ExecuteReader(); reader.Read();
            check(reader.GetInt64(0) == 0, "观察器不能持有长期读事务阻挡WAL重用");
        }
        using (var transaction = writer.BeginTransaction())
        using (var update = writer.CreateCommand())
        {
            update.Transaction = transaction; update.CommandText = "UPDATE threads SET tokens_used=202";
            update.ExecuteNonQuery(); transaction.Commit();
        }
        File.SetLastWriteTimeUtc(path, databaseTime); File.SetLastWriteTimeUtc(path + "-wal", walTime);
        database.Refresh(); wal.Refresh();
        check(database.Length == databaseLength && wal.Length == walLength && database.LastWriteTimeUtc == databaseTime && wal.LastWriteTimeUtc == walTime,
            "夹具必须保证DB/WAL长度和修改时间全部不变");
        using (var targetWriter = Open(target))
            Execute(targetWriter, "CREATE TRIGGER fixture_meta_abort BEFORE INSERT ON session_snapshots BEGIN SELECT RAISE(ABORT,'fixture metadata failure'); END");
        using (var failed = JsonDocument.Parse(await service.RefreshAsync()))
        {
            check(failed.RootElement.GetProperty("error").ValueKind == JsonValueKind.String, "必须真实触发元数据写入失败");
            check(failed.RootElement.GetProperty("report").GetProperty("metadata_sessions")[0].GetProperty("total_tokens").GetInt64() == 101, "失败应保留上次快照而非发布零值");
        }
        using (var targetWriter = Open(target)) Execute(targetWriter, "DROP TRIGGER fixture_meta_abort");
        using (var refreshed = JsonDocument.Parse(await service.RefreshAsync()))
            check(refreshed.RootElement.GetProperty("report").GetProperty("metadata_sessions")[0].GetProperty("total_tokens").GetInt64() == 202,
                "WAL外部提交即使文件签名相同也必须刷新元数据");
        await service.StopAsync();
        check(Scalar(writer, "SELECT tokens_used FROM threads") == 202, "观察和停止均不得改写源计数");
        writer.Dispose();
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        check(exclusive.CanRead, "Stop必须释放持久观察连接的源文件句柄");
    }

    static async Task CheckAccountAsync(string directory, string home, DateTimeOffset now, Action<bool, string> check)
    {
        using var quotaJson = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            rateLimitsByLimitId = new Dictionary<string, object>
            {
                ["codex"] = new { primary = new { usedPercent = 0, windowDurationMins = 10080, resetsAt = now.AddHours(1).ToUnixTimeSeconds() } },
                ["other"] = new { secondary = new { usedPercent = 80, resetsAt = now.AddHours(-1).ToUnixTimeSeconds() } }
            }
        }));
        using var usageJson = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            summary = new { lifetimeTokens = 123456L },
            dailyUsageBuckets = new[] { new { startDate = now.ToString("yyyy-MM-dd"), tokens = 0L } }
        }));
        var quota = CodexAccountClient.ParseQuota(quotaJson.RootElement, now);
        var usage = CodexAccountClient.ParseUsage(usageJson.RootElement, now);
        var reading = new CodexAccountReading("chatgpt", "pro", "ok", "ok", quota, usage);
        var called = 0;
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var service = new AiUsageService(Path.Combine(directory, "account.db"), home, async (_, token) =>
        {
            switch (++called)
            {
                case 1: return reading;
                case 2: return CodexAccountReading.Unavailable("timeout");
                case 3: return reading with { Usage = null, UsageStatus = "unsupported" };
                case 4: return new("api_key", null, "auth_unsupported", "auth_unsupported");
                default:
                    waiting.SetResult();
                    await Task.Delay(Timeout.Infinite, token);
                    return reading;
            }
        });
        using (var scan = JsonDocument.Parse(await service.RefreshAsync()))
        {
            check(called == 0 && scan.RootElement.GetProperty("account_quota").GetProperty("state").GetString() == "not_checked",
                "本地扫描不能启动账户查询");
            check(scan.RootElement.GetProperty("account_usage").GetProperty("lifetime_tokens").ValueKind == JsonValueKind.Null,
                "从未读取账户时 Token 用量必须未知");
        }
        using (var first = JsonDocument.Parse(await service.RefreshAccountAsync()))
        {
            var result = first.RootElement.GetProperty("account_quota");
            var windows = result.GetProperty("windows");
            var primary = windows[0].GetProperty("primary");
            var expired = windows[1].GetProperty("secondary");
            check(result.GetProperty("state").GetString() == "fresh" && windows.GetArrayLength() == 2, "套餐桶必须独立保留");
            check(primary.GetProperty("remaining_percent").GetDouble() == 100 &&
                primary.GetProperty("remaining_seconds").GetInt64() > 0, "实际零使用量应转换为全额剩余与重置倒计时");
            check(expired.GetProperty("status").GetString() == "awaiting_refresh" &&
                expired.GetProperty("remaining_percent").ValueKind == JsonValueKind.Null, "已重置窗口不得推断当前剩余");
            var tokens = first.RootElement.GetProperty("account_usage");
            check(tokens.GetProperty("lifetime_tokens").GetInt64() == 123456 &&
                tokens.GetProperty("daily_usage_buckets")[0].GetProperty("tokens").GetInt64() == 0, "账户总量与实际日用量零值均应保留");
        }
        await service.RefreshAccountAsync();
        using (var stale = JsonDocument.Parse(await service.ReportAsync()))
        {
            var result = stale.RootElement.GetProperty("account_quota");
            var primary = result.GetProperty("windows")[0].GetProperty("primary");
            check(result.GetProperty("state").GetString() == "stale" && result.GetProperty("attempt_status").GetString() == "timeout" &&
                primary.GetProperty("remaining_percent").ValueKind == JsonValueKind.Null, "刷新失败只保留可识别的历史快照");
        }
        using (var partial = JsonDocument.Parse(await service.RefreshAccountAsync()))
        {
            var result = partial.RootElement.GetProperty("account_usage");
            check(result.GetProperty("state").GetString() == "unsupported" &&
                result.GetProperty("lifetime_tokens").ValueKind == JsonValueKind.Null, "认证成功后的部分结果不能混入旧账户用量");
        }
        using (var unsupported = JsonDocument.Parse(await service.RefreshAccountAsync()))
        {
            var result = unsupported.RootElement.GetProperty("account_quota");
            check(result.GetProperty("auth_mode").GetString() == "api_key" &&
                result.GetProperty("windows").GetArrayLength() == 0 &&
                unsupported.RootElement.GetProperty("account_usage").GetProperty("lifetime_tokens").ValueKind == JsonValueKind.Null,
                "切换到 API key 必须清除先前订阅快照");
        }
        var pending = service.RefreshAccountAsync();
        await waiting.Task;
        using (var coalesced = JsonDocument.Parse(await service.RefreshAccountAsync()))
            check(called == 5 && coalesced.RootElement.GetProperty("account_quota").GetProperty("refreshing").GetBoolean(),
                "账户查询重复触发时只能有一个在途请求");
        await service.StopAsync();
        check(pending.IsCanceled, "退出必须取消并收束在途账户查询");
    }

    static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }

    static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar());
    }
}

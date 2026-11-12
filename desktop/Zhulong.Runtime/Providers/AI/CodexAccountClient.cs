using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Zhulong.Runtime.Providers.AI;

internal sealed record AccountQuotaBucket(string LimitId, string? LimitName, QuotaWindow? Primary, QuotaWindow? Secondary);
internal sealed record AccountUsageDay(string StartDate, long Tokens);
internal sealed record AccountQuotaReading(DateTimeOffset FetchedAt, IReadOnlyList<AccountQuotaBucket> Buckets);
internal sealed record AccountUsageReading(DateTimeOffset FetchedAt, long? LifetimeTokens, IReadOnlyList<AccountUsageDay>? Days);
internal sealed record CodexAccountReading(
    string? AuthMode, string? PlanType, string QuotaStatus, string UsageStatus,
    AccountQuotaReading? Quota = null, AccountUsageReading? Usage = null)
{
    public static CodexAccountReading Unavailable(string status) => new(null, null, status, status);
}

internal static class CodexAccountClient
{
    public static async Task<CodexAccountReading> ReadAsync(string home, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var executable = FindExecutable();
        if (executable is null) return CodexAccountReading.Unavailable("cli_unavailable");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetTempPath(),
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8
        };
        start.Environment["CODEX_HOME"] = home;
        foreach (var argument in new[] { "app-server", "--listen", "stdio://",
            "-c", "analytics.enabled=false", "-c", "check_for_update_on_startup=false" })
            start.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = start };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var status = CodexAccountReading.Unavailable("unavailable");
        var started = false;
        try
        {
            started = process.Start();
            if (!started) return status;
            var io = deadline.Token;
            _ = DrainErrorAsync(process.StandardError.BaseStream, io);
            var reader = new BoundedRpcReader(process.StandardOutput);
            var writer = process.StandardInput;
            await SendAsync(writer, new
            {
                id = 1, method = "initialize",
                @params = new
                {
                    clientInfo = new { name = "zhulong", version = "1.0" },
                    capabilities = new { experimentalApi = true }
                }
            }, io).ConfigureAwait(false);
            var init = await reader.ReadReplyAsync(io).ConfigureAwait(false);
            if (ReplyId(init) != 1 || Get(init, "result").ValueKind != JsonValueKind.Object)
                return CodexAccountReading.Unavailable("cli_unsupported");
            await SendAsync(writer, new { method = "initialized" }, io).ConfigureAwait(false);
            await SendAsync(writer, new { id = 2, method = "account/read", @params = new { refreshToken = false } }, io)
                .ConfigureAwait(false);
            var authReply = await reader.ReadReplyAsync(io).ConfigureAwait(false);
            var authResult = Get(authReply, "result");
            if (ReplyId(authReply) != 2 || authResult.ValueKind != JsonValueKind.Object)
                return CodexAccountReading.Unavailable(ReplyFailure(authReply));
            var account = Get(authResult, "account");
            if (account.ValueKind == JsonValueKind.Undefined) return status;
            var authMode = String(account, "type");
            var plan = String(account, "planType");
            if (authMode != "chatgpt")
            {
                var unavailable = account.ValueKind == JsonValueKind.Null ? "not_signed_in" : "auth_unsupported";
                return new(authMode == "apiKey" ? "api_key" : null, null, unavailable, unavailable);
            }
            status = new("chatgpt", plan, "unavailable", "unavailable");
            await SendAsync(writer, new { id = 3, method = "account/rateLimits/read" }, io).ConfigureAwait(false);
            await SendAsync(writer, new { id = 4, method = "account/usage/read" }, io).ConfigureAwait(false);
            for (var replies = 0; replies < 2; replies++)
            {
                var reply = await reader.ReadReplyAsync(io).ConfigureAwait(false);
                var result = Get(reply, "result");
                var fetched = DateTimeOffset.UtcNow;
                if (ReplyId(reply) == 3)
                {
                    var quota = ParseQuota(result, fetched);
                    status = status with { Quota = quota, QuotaStatus = quota is not null ? "ok" : ReplyFailure(reply) };
                }
                if (ReplyId(reply) == 4)
                {
                    var usage = ParseUsage(result, fetched);
                    status = status with { Usage = usage, UsageStatus = usage is not null ? "ok" : ReplyFailure(reply) };
                }
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            status = status with
            {
                QuotaStatus = status.QuotaStatus == "unavailable" ? "timeout" : status.QuotaStatus,
                UsageStatus = status.UsageStatus == "unavailable" ? "timeout" : status.UsageStatus
            };
        }
        catch (Exception exception) when (exception is IOException or JsonException or Win32Exception or InvalidOperationException)
        {
            // 协议或启动错误只以状态暴露，避免转发可能包含账户信息的诊断内容。
        }
        finally
        {
            if (started) await StopProcessAsync(process).ConfigureAwait(false);
        }
        token.ThrowIfCancellationRequested();
        return status;
    }

    internal static AccountQuotaReading? ParseQuota(JsonElement result, DateTimeOffset fetched)
    {
        var buckets = new List<AccountQuotaBucket>();
        var multiple = Get(result, "rateLimitsByLimitId");
        if (multiple.ValueKind == JsonValueKind.Object)
            foreach (var entry in multiple.EnumerateObject().Take(32))
                AddBucket(entry.Value, entry.Name);
        if (buckets.Count == 0) AddBucket(Get(result, "rateLimits"), "codex");
        return buckets.Count == 0 ? null : new(fetched, buckets);

        void AddBucket(JsonElement bucket, string fallbackId)
        {
            var primary = Window(bucket, "primary");
            var secondary = Window(bucket, "secondary");
            if (primary is not null || secondary is not null)
                buckets.Add(new(String(bucket, "limitId") ?? fallbackId, String(bucket, "limitName"), primary, secondary));
        }
    }

    internal static AccountUsageReading? ParseUsage(JsonElement result, DateTimeOffset fetched)
    {
        var lifetime = Integer(Get(result, "summary"), "lifetimeTokens");
        var daily = Get(result, "dailyUsageBuckets");
        var days = new SortedDictionary<string, long>(StringComparer.Ordinal);
        if (daily.ValueKind == JsonValueKind.Array)
            foreach (var bucket in daily.EnumerateArray())
            {
                var date = String(bucket, "startDate");
                var tokens = Integer(bucket, "tokens");
                if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ||
                    tokens is null or < 0) continue;
                days[date!] = tokens.Value;
                if (days.Count > 90) days.Remove(days.First().Key);
            }
        if ((lifetime is null or < 0) && days.Count == 0) return null;
        var buckets = daily.ValueKind == JsonValueKind.Array
            ? days.Select(x => new AccountUsageDay(x.Key, x.Value)).ToArray() : null;
        return new(fetched, lifetime is >= 0 ? lifetime : null, buckets);
    }

    static QuotaWindow? Window(JsonElement bucket, string name)
    {
        var data = Get(bucket, name);
        if (data.ValueKind != JsonValueKind.Object) return null;
        var used = Get(data, "usedPercent");
        double? percent = used.ValueKind == JsonValueKind.Number && used.TryGetDouble(out var amount) &&
            double.IsFinite(amount) && amount >= 0 ? amount : null;
        var minutes = Integer(data, "windowDurationMins");
        var epoch = Integer(data, "resetsAt");
        var reset = epoch is > 0 and <= 253402300799 ? DateTimeOffset.FromUnixTimeSeconds(epoch.Value) : (DateTimeOffset?)null;
        return percent is null && minutes is not > 0 && reset is null ? null
            : new(name, percent, minutes is > 0 ? minutes : null, reset);
    }

    static string ReplyFailure(JsonElement reply) => Integer(Get(reply, "error"), "code") == -32601 ? "unsupported" : "unavailable";
    static long? ReplyId(JsonElement reply) => Integer(reply, "id");
    static JsonElement Get(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;
    static string? String(JsonElement element, string name) =>
        Get(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
    static long? Integer(JsonElement element, string name) =>
        Get(element, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt64(out var number) ? number : null;

    static async Task SendAsync(StreamWriter writer, object message, CancellationToken token)
    {
        await writer.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), token).ConfigureAwait(false);
        await writer.FlushAsync(token).ConfigureAwait(false);
    }

    static async Task DrainErrorAsync(Stream stderr, CancellationToken token)
    {
        try { await stderr.CopyToAsync(Stream.Null, 4096, token).ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException) { }
    }

    static async Task StopProcessAsync(Process process)
    {
        try { process.StandardInput.Close(); }
        catch (IOException) { }
        using var grace = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        try { await process.WaitForExitAsync(grace.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
                using var killed = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await process.WaitForExitAsync(killed.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or OperationCanceledException) { }
        }
    }

    static string? FindExecutable()
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var path = directory.Trim('"');
            if (!Path.IsPathFullyQualified(path)) continue;
            var file = Path.Combine(path, "codex.exe");
            if (File.Exists(file)) return file;
        }
        var bin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
        try
        {
            if (Directory.Exists(bin))
                return Directory.EnumerateDirectories(bin).Take(32)
                    .OrderByDescending(Directory.GetLastWriteTimeUtc).Select(path => Path.Combine(path, "codex.exe"))
                    .FirstOrDefault(File.Exists);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        return null;
    }

    sealed class BoundedRpcReader(StreamReader reader)
    {
        readonly char[] buffer = new char[4096];
        readonly StringBuilder line = new();
        int position, end, total, lines;

        public async Task<JsonElement> ReadReplyAsync(CancellationToken token)
        {
            while (lines < 128)
            {
                token.ThrowIfCancellationRequested();
                if (position == end)
                {
                    end = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                    position = 0;
                    total += end;
                    if (end == 0 || total > 256 * 1024) break;
                }
                var newline = Array.IndexOf(buffer, '\n', position, end - position);
                var length = (newline < 0 ? end : newline) - position;
                line.Append(buffer, position, length);
                position += length;
                if (newline < 0) continue;
                position++;
                lines++;
                var json = line.ToString();
                line.Clear();
                if (string.IsNullOrWhiteSpace(json)) continue;
                using var document = JsonDocument.Parse(json);
                if (ReplyId(document.RootElement) is not null &&
                    (Get(document.RootElement, "result").ValueKind != JsonValueKind.Undefined ||
                     Get(document.RootElement, "error").ValueKind != JsonValueKind.Undefined))
                    return document.RootElement.Clone();
            }
            throw new IOException("Codex App Server 响应缺失或超出读取预算。");
        }
    }
}

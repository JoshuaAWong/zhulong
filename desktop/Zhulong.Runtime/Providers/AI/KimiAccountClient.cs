using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Zhulong.Runtime.Providers.AI;

internal static class KimiAccountClient
{
    static readonly (string Name, long? Minutes)[] KnownWindows =
        [("limit5h", 300), ("limit7d", 10080), ("monthTotal", null), ("monthCode", null)];

    public static async Task<ProviderAccountReading> ReadAsync(string home, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!File.Exists(Path.Combine(home, "credentials", "kimi-code.json"))) return Failed("not_signed_in");
        var executable = FindExecutable();
        if (executable is null) return Failed("cli_unavailable");

        using var process = new Process
        {
            StartInfo = new(executable)
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetTempPath(),
                RedirectStandardOutput = true, RedirectStandardError = true
            }
        };
        process.StartInfo.Environment["KIMI_CODE_HOME"] = home;
        foreach (var argument in new[] { "web", "--no-open", "--host", "127.0.0.1", "--port", "0" })
            process.StartInfo.ArgumentList.Add(argument);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        using var http = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseProxy = false, UseCookies = false, ConnectTimeout = TimeSpan.FromMilliseconds(500)
        });
        var started = false;
        try
        {
            started = process.Start();
            if (!started) return Failed("cli_unavailable");
            // 启动输出含本地访问令牌。只排空管道，绝不记录原文。
            _ = DrainAsync(process.StandardOutput.BaseStream, deadline.Token);
            _ = DrainAsync(process.StandardError.BaseStream, deadline.Token);
            var io = deadline.Token;
            var port = await WaitForOwnedPortAsync(process, home, io).ConfigureAwait(false);
            if (port == 0) return Failed("cli_unsupported");
            http.BaseAddress = new Uri($"http://127.0.0.1:{port}/");
            if (!await WaitForListenerAsync(process, http, io).ConfigureAwait(false)) return Failed("cli_unsupported");
            var localBearer = await ReadLocalTokenAsync(home, io).ConfigureAwait(false);
            if (localBearer is null || process.HasExited) return Failed("unavailable");
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", localBearer);
            // 只使用登记中属于本次启动进程的端口。既有实例和它们的会话不参与查询。
            var auth = await GetDataAsync(http, "auth", io).ConfigureAwait(false);
            if (auth.ValueKind == JsonValueKind.Undefined) return Failed("unsupported");
            var authState = String(Get(auth, "managed_provider"), "status");
            if (authState is "unauthenticated" or "revoked")
                return Failed(authState == "revoked" ? "auth_revoked" : "not_signed_in");

            var profileTask = GetDataAsync(http, "oauth/userinfo", io);
            var usageTask = GetDataAsync(http, "oauth/usage", io);
            await Task.WhenAll(profileTask, usageTask).ConfigureAwait(false);
            return ParseReading(await profileTask.ConfigureAwait(false), await usageTask.ConfigureAwait(false), authState);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return Failed("timeout"); }
        catch (Exception exception) when (exception is HttpRequestException or IOException or
            UnauthorizedAccessException or Win32Exception or JsonException or FormatException or InvalidOperationException)
        {
            // 错误响应可能含账号资料；只向上层返回状态。
            return Failed("unavailable");
        }
        finally
        {
            if (started) await StopOwnedProcessAsync(process, http).ConfigureAwait(false);
        }
    }

    internal static ProviderAccountReading ParseReading(JsonElement profile, JsonElement usage, string? authState)
    {
        var account = Get(profile, "userInfo");
        var id = Get(account, "userId");
        var userId = id.ValueKind == JsonValueKind.String ? id.GetString()
            : id.ValueKind == JsonValueKind.Number ? id.GetRawText() : null;
        var identity = string.IsNullOrWhiteSpace(userId) ? null : Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"kimi-code:{String(account, "region")}:{userId}")));
        var plan = String(account, "userLevelName");
        if (String(usage, "kind") != "ok")
        {
            var status = Get(usage, "status");
            var state = status.ValueKind == JsonValueKind.Number && status.TryGetInt32(out var code)
                ? code switch { 401 => "not_signed_in", 403 => "unavailable", 404 => "unsupported", _ => "unavailable" }
                : authState == "expired" ? "auth_expired" : "unavailable";
            return new(state, identity, plan, []);
        }

        var limits = Get(Get(usage, "quota"), "usages");
        var windows = new List<ProviderQuotaWindow>();
        foreach (var (name, minutes) in KnownWindows)
        {
            var window = Get(limits, name);
            var ratio = Get(window, "usedRatio");
            var used = ratio.ValueKind == JsonValueKind.Number && ratio.TryGetDouble(out var amount) && amount is >= 0 and <= 1
                ? amount * 100 : (double?)null;
            var resets = DateTimeOffset.TryParse(String(window, "resetAt"), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var parsed) ? parsed : (DateTimeOffset?)null;
            if (used is not null || resets is not null) windows.Add(new(name, used, resets, minutes));
        }
        return new(windows.Count == 0 ? "unsupported" : "ok", identity, plan, windows);
    }

    static async Task<int> WaitForOwnedPortAsync(Process process, string home, CancellationToken token)
    {
        var directory = Path.Combine(home, "server", "instances");
        var startedAt = new DateTimeOffset(process.StartTime).ToUnixTimeMilliseconds() - 1000;
        while (!process.HasExited)
        {
            token.ThrowIfCancellationRequested();
            if (Directory.Exists(directory))
                foreach (var path in Directory.EnumerateFiles(directory, "*.json").Take(64))
                {
                    try
                    {
                        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        if (file.Length > 16384) continue;
                        using var doc = await JsonDocument.ParseAsync(file, cancellationToken: token).ConfigureAwait(false);
                        var id = Get(doc.RootElement, "pid");
                        var port = Get(doc.RootElement, "port");
                        var stamp = Get(doc.RootElement, "started_at");
                        if (id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out var pid) && pid == process.Id &&
                            stamp.ValueKind == JsonValueKind.Number && stamp.TryGetInt64(out var registeredAt) && registeredAt >= startedAt &&
                            String(doc.RootElement, "host") == "127.0.0.1" &&
                            port.ValueKind == JsonValueKind.Number && port.TryGetInt32(out var value) && value is > 0 and <= 65535)
                            return value;
                    }
                    catch (Exception exception) when (exception is IOException or JsonException) { }
                }
            await Task.Delay(100, token).ConfigureAwait(false);
        }
        return 0;
    }

    static async Task<bool> WaitForListenerAsync(Process process, HttpClient http, CancellationToken token)
    {
        while (!process.HasExited)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var reply = await http.GetAsync("api/v1/healthz", HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                if (reply.IsSuccessStatusCode) return !process.HasExited;
            }
            catch (HttpRequestException) { }
            await Task.Delay(100, token).ConfigureAwait(false);
        }
        return false;
    }

    static async Task<string?> ReadLocalTokenAsync(string home, CancellationToken token)
    {
        var path = Path.Combine(home, "server.token");
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (file.Length is < 1 or > 1024) return null;
        using var reader = new StreamReader(file, Encoding.UTF8);
        var buffer = new char[1025];
        var length = await reader.ReadBlockAsync(buffer.AsMemory(), token).ConfigureAwait(false);
        if (length == buffer.Length) return null;
        var data = new string(buffer, 0, length).Trim();
        return data.Length > 0 && data.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_') ? data : null;
    }

    static async Task<JsonElement> GetDataAsync(HttpClient http, string endpoint, CancellationToken token)
    {
        using var response = await http.GetAsync("api/v1/" + endpoint, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return default;
        response.EnsureSuccessStatusCode();
        await response.Content.LoadIntoBufferAsync(128 * 1024, token).ConfigureAwait(false);
        using var body = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(body, cancellationToken: token).ConfigureAwait(false);
        return Get(json.RootElement, "data").Clone();
    }

    static async Task StopOwnedProcessAsync(Process process, HttpClient http)
    {
        if (process.HasExited) return;
        using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try
        {
            if (http.BaseAddress is not null && http.DefaultRequestHeaders.Authorization is not null)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/shutdown");
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, grace.Token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode) await process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException) { }
        if (process.HasExited) return;
        try
        {
            process.Kill(entireProcessTree: true);
            using var killed = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await process.WaitForExitAsync(killed.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or OperationCanceledException) { }
    }

    static async Task DrainAsync(Stream stream, CancellationToken token)
    {
        try { await stream.CopyToAsync(Stream.Null, 4096, token).ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException) { }
    }

    internal static string? FindExecutable()
    {
        var defaultPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".kimi-code", "bin", "kimi.exe");
        if (File.Exists(defaultPath)) return defaultPath;
        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var directory = entry.Trim('"');
            if (!Path.IsPathFullyQualified(directory)) continue;
            var path = Path.Combine(directory, "kimi.exe");
            if (File.Exists(path)) return path;
        }
        return null;
    }

    static ProviderAccountReading Failed(string state) => new(state, null, null, []);
    static JsonElement Get(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) ? property : default;
    static string? String(JsonElement value, string name) =>
        Get(value, name) is { ValueKind: JsonValueKind.String } property ? property.GetString() : null;
}

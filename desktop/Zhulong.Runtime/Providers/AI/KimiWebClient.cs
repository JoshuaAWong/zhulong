using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Zhulong.Runtime.Providers.AI;

public sealed class KimiDeviceAuthorization
{
    readonly long started = Stopwatch.GetTimestamp();
    readonly TimeSpan lifetime;

    internal KimiDeviceAuthorization(bool domestic, Uri verificationUri, string userCode, string deviceCode,
        int expiresIn, int interval)
    {
        Domestic = domestic;
        VerificationUri = verificationUri;
        UserCode = userCode;
        DeviceCode = deviceCode;
        lifetime = TimeSpan.FromSeconds(expiresIn);
        ExpiresAt = DateTimeOffset.UtcNow.Add(lifetime);
        Interval = interval;
    }

    public Uri VerificationUri { get; }
    public string UserCode { get; }
    public DateTimeOffset ExpiresAt { get; }
    internal bool Domestic { get; }
    internal string DeviceCode { get; }
    internal int Interval { get; }
    internal TimeSpan Remaining => lifetime - Stopwatch.GetElapsedTime(started);
}

internal sealed record KimiWebTokens(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);
internal sealed record KimiWebIdentity(string Identity, string? PlanType);

public sealed class KimiWebAuthException : Exception
{
    internal KimiWebAuthException(string state) : base(state switch
    {
        "auth_expired" => "Kimi 账户授权已失效，请重新绑定。",
        "not_configured" => "此账号已移除，请重新添加。",
        "duplicate_account" => "这个 Kimi 账号已绑定，请在浏览器切换账号后重试。",
        "account_mismatch" => "这次授权的是另一个 Kimi 账号。请重新授权此账号，或使用「绑定 Kimi 账号」。",
        "authorization_expired" => "Kimi 授权码已过期，请重新发起授权。",
        "authorization_denied" => "Kimi 授权未获同意。",
        "rate_limited" => "Kimi 请求过于频繁，请稍后重试。",
        "timeout" => "Kimi 服务响应超时，请稍后重试。",
        "unsupported" => "Kimi 服务返回了暂不支持的响应。",
        _ => "暂时无法访问 Kimi 服务，请稍后重试。"
    }) => State = state;

    public string State { get; }
}

internal sealed class KimiWebClient(HttpClient client)
{
    // 使用 Kimi 官方设备授权协议。地区绑定决定授权和额度两个域名。
    const string ClientId = "17e5f671-d194-4dfb-9706-5516cb48c098";
    const int MaxAuthorizationSeconds = 1800;
    static readonly HttpClient SharedClient = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        Timeout = TimeSpan.FromSeconds(12), MaxResponseContentBufferSize = 128 * 1024
    };
    static readonly (string Wire, string Name, long? Minutes)[] KnownWindows =
    [
        ("limit_5h", "limit5h", 300), ("limit_7d", "limit7d", 10080),
        ("limit_month_total", "monthTotal", null), ("limit_month_code", "monthCode", null)
    ];

    internal KimiWebClient() : this(SharedClient) { }

    internal async Task<KimiDeviceAuthorization> StartAsync(bool domestic, CancellationToken token)
    {
        var (status, json) = await PostAsync(domestic, "device_authorization", [], token).ConfigureAwait(false);
        EnsureSuccess(status);
        var deviceCode = String(json, "device_code");
        var userCode = String(json, "user_code");
        var url = String(json, "verification_uri_complete") ?? String(json, "verification_uri");
        if (!IsCredential(deviceCode) || !IsCredential(userCode) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsOfficialVerificationUri(uri))
            throw new KimiWebAuthException("unsupported");
        var expires = Math.Min(PositiveInt(json, "expires_in") ?? 900, MaxAuthorizationSeconds);
        var interval = Math.Min(PositiveInt(json, "interval") ?? 5, expires);
        return new(domestic, uri, userCode!, deviceCode!, expires, interval);
    }

    internal async Task<KimiWebTokens> CompleteAsync(KimiDeviceAuthorization authorization, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var remaining = authorization.Remaining;
        if (remaining <= TimeSpan.Zero) throw new KimiWebAuthException("authorization_expired");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(remaining);
        var interval = authorization.Interval;
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(interval), deadline.Token).ConfigureAwait(false);
                var (status, json) = await PostAsync(authorization.Domestic, "token",
                    [new("grant_type", "urn:ietf:params:oauth:grant-type:device_code"), new("device_code", authorization.DeviceCode)],
                    deadline.Token).ConfigureAwait(false);
                deadline.Token.ThrowIfCancellationRequested();
                switch (String(json, "error"))
                {
                    case "authorization_pending": continue;
                    case "slow_down": interval += 5; continue;
                    case "expired_token": throw new KimiWebAuthException("authorization_expired");
                    case "access_denied": throw new KimiWebAuthException("authorization_denied");
                }
                EnsureSuccess(status);
                return ParseTokens(json, null);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new KimiWebAuthException("authorization_expired");
        }
    }

    internal async Task<KimiWebTokens> RefreshAsync(bool domestic, string refreshToken, CancellationToken token)
    {
        if (!IsCredential(refreshToken)) throw new KimiWebAuthException("auth_expired");
        var (status, json) = await PostAsync(domestic, "token",
            [new("grant_type", "refresh_token"), new("refresh_token", refreshToken)], token).ConfigureAwait(false);
        if (String(json, "error") == "invalid_grant") throw new KimiWebAuthException("auth_expired");
        EnsureSuccess(status);
        return ParseTokens(json, refreshToken);
    }

    internal async Task<KimiWebIdentity> ReadIdentityAsync(bool domestic, string accessToken, CancellationToken token)
    {
        var json = await ReadAsync(domestic, "me", accessToken, token).ConfigureAwait(false);
        var id = Get(json, "user_id");
        var userId = id.ValueKind == JsonValueKind.Number ? id.GetRawText() : String(json, "user_id");
        if (string.IsNullOrWhiteSpace(userId)) throw new KimiWebAuthException("unsupported");
        // 与 CLI 读取路径使用同一指纹，网页仅收到去重状态。
        var identity = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"kimi-code:{String(json, "region")}:{userId}")));
        var plan = String(json, "user_level_name");
        return new(identity, string.IsNullOrWhiteSpace(plan) ? null : plan);
    }

    internal async Task<IReadOnlyList<ProviderQuotaWindow>> ReadQuotaAsync(bool domestic, string accessToken, CancellationToken token)
    {
        var json = await ReadAsync(domestic, "usages", accessToken, token).ConfigureAwait(false);
        var limits = Get(json, "usages");
        var windows = new List<ProviderQuotaWindow>();
        foreach (var (wire, name, minutes) in KnownWindows)
        {
            var window = Get(limits, wire);
            var ratio = Get(window, "used_ratio");
            var hasAmount = ratio.ValueKind == JsonValueKind.Number ? ratio.TryGetDouble(out var used)
                : double.TryParse(String(window, "used_ratio"), NumberStyles.Float, CultureInfo.InvariantCulture, out used);
            var percent = hasAmount && used is >= 0 and <= 1 ? used * 100 : (double?)null;
            var resets = DateTimeOffset.TryParse(String(window, "reset_time"), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var stamp) ? stamp : (DateTimeOffset?)null;
            if (percent is not null || resets is not null) windows.Add(new(name, percent, resets, minutes));
        }
        if (windows.Count == 0) throw new KimiWebAuthException("unsupported");
        return windows;
    }

    async Task<JsonElement> ReadAsync(bool domestic, string path, string accessToken, CancellationToken token)
    {
        if (!IsCredential(accessToken)) throw new KimiWebAuthException("auth_expired");
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.kimi.{(domestic ? "com" : "ai")}/coding/v1/{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var (status, json) = await SendAsync(request, false, token).ConfigureAwait(false);
        EnsureSuccess(status);
        return json;
    }

    async Task<(HttpStatusCode Status, JsonElement Json)> PostAsync(bool domestic, string path,
        KeyValuePair<string, string>[] fields, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://auth.kimi.{(domestic ? "com" : "ai")}/api/oauth/{path}")
        {
            Content = new FormUrlEncodedContent([new("client_id", ClientId), .. fields])
        };
        return await SendAsync(request, true, token).ConfigureAwait(false);
    }

    async Task<(HttpStatusCode Status, JsonElement Json)> SendAsync(HttpRequestMessage request, bool readError, CancellationToken token)
    {
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        try
        {
            using var response = await client.SendAsync(request, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode && !readError) return (response.StatusCode, default);
            await using var body = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            try
            {
                using var doc = await JsonDocument.ParseAsync(body, cancellationToken: token).ConfigureAwait(false);
                return (response.StatusCode, doc.RootElement.Clone());
            }
            catch (JsonException) when (!response.IsSuccessStatusCode) { return (response.StatusCode, default); }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new KimiWebAuthException("timeout"); }
        catch (JsonException) { throw new KimiWebAuthException("unsupported"); }
        catch (Exception error) when (error is HttpRequestException or IOException) { throw new KimiWebAuthException("unavailable"); }
    }

    static KimiWebTokens ParseTokens(JsonElement json, string? priorRefreshToken)
    {
        var access = String(json, "access_token");
        var refresh = String(json, "refresh_token") ?? priorRefreshToken;
        var expires = PositiveInt(json, "expires_in");
        if (!IsCredential(access) || !IsCredential(refresh) || expires is null)
            throw new KimiWebAuthException("unsupported");
        return new(access!, refresh!, DateTimeOffset.UtcNow.AddSeconds(expires.Value));
    }

    static void EnsureSuccess(HttpStatusCode status)
    {
        if ((int)status is >= 200 and < 300) return;
        throw new KimiWebAuthException(status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "auth_expired",
            HttpStatusCode.NotFound => "unsupported",
            HttpStatusCode.TooManyRequests => "rate_limited",
            _ => "unavailable"
        });
    }

    static bool IsOfficialVerificationUri(Uri uri) => uri.Scheme == Uri.UriSchemeHttps &&
        uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
        (uri.IdnHost is "kimi.com" or "kimi.ai" ||
         uri.IdnHost.EndsWith(".kimi.com", StringComparison.Ordinal) || uri.IdnHost.EndsWith(".kimi.ai", StringComparison.Ordinal));

    static bool IsCredential(string? value) => value is { Length: > 0 and <= 4096 } && value.All(c => c is >= '!' and <= '~');
    static JsonElement Get(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) ? property : default;
    static string? String(JsonElement value, string name) =>
        Get(value, name) is { ValueKind: JsonValueKind.String } property ? property.GetString() : null;
    static int? PositiveInt(JsonElement value, string name)
    {
        var property = Get(value, name);
        var valid = property.ValueKind == JsonValueKind.Number ? property.TryGetInt32(out var number)
            : int.TryParse(String(value, name), NumberStyles.None, CultureInfo.InvariantCulture, out number);
        return valid && number > 0 ? number : null;
    }
}

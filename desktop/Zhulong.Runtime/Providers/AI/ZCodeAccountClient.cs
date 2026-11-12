using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Zhulong.Runtime.Providers.AI;

// 与智谱官方 glm-plan-usage 插件使用相同的个人套餐查询接口。
internal static class ZCodeAccountClient
{
    const string QuotaPath = "/api/monitor/usage/quota/limit";
    static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        Timeout = TimeSpan.FromSeconds(8), MaxResponseContentBufferSize = 256 * 1024
    };

    internal static Task<ProviderAccountReading> ReadAsync(string key, bool domestic, CancellationToken token) =>
        ReadAsync(Client, key, domestic, token);

    internal static async Task<ProviderAccountReading> ReadAsync(HttpClient client, string key, bool domestic,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(key)) return Failed("not_configured");
        if (key.Length > 2048 || key.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
            return Failed("auth_rejected");
        using var request = new HttpRequestMessage(HttpMethod.Get,
            (domestic ? "https://open.bigmodel.cn" : "https://api.z.ai") + QuotaPath);
        // 官方插件传原始 Key。凭据只通过固定主机的请求头发送。
        request.Headers.TryAddWithoutValidation("Authorization", key);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        try
        {
            using var response = await client.SendAsync(request, token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return Failed("auth_rejected");
            if (response.StatusCode == HttpStatusCode.TooManyRequests) return Failed("rate_limited");
            if (!response.IsSuccessStatusCode) return Failed("unavailable");
            await using var body = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(body, cancellationToken: token).ConfigureAwait(false);
            return ParseReading(document.RootElement);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return Failed("timeout"); }
        catch (JsonException) { return Failed("unsupported"); }
        catch (Exception error) when (error is HttpRequestException or IOException) { return Failed("unavailable"); }
    }

    internal static ProviderAccountReading ParseReading(JsonElement response)
    {
        var code = Integer(Get(response, "code"));
        if (code is 401 or 403) return Failed("auth_rejected");
        if (code == 429) return Failed("rate_limited");
        if (Get(response, "success").ValueKind == JsonValueKind.False || code is not (null or 0 or 200))
            return Failed("unavailable");
        var data = Get(response, "data");
        var limits = Get(data, "limits");
        if (limits.ValueKind != JsonValueKind.Array) return Failed("unsupported");
        var windows = new Dictionary<string, ProviderQuotaWindow>(StringComparer.Ordinal);
        foreach (var row in limits.EnumerateArray())
        {
            var type = Text(Get(row, "type"));
            var unit = Integer(Get(row, "unit"));
            var count = Integer(Get(row, "number"));
            var (name, minutes, amountUnit) = (type, unit, count) switch
            {
                ("CREDIT_LIMIT", 3, 5) => ("limit5h", 300L, "credits"),
                ("CREDIT_LIMIT", 6, 1) => ("limit7d", 10080L, "credits"),
                ("TOKENS_LIMIT", 3, 5) or ("TOKENS_LIMIT", null, null) => ("limit5h", 300L, "tokens"),
                ("TOKENS_LIMIT", 6, 1 or 7) => ("limit7d", 10080L, "tokens"),
                ("TIME_LIMIT", 5, 1) or ("TIME_LIMIT", null, null) => ("monthMcp", (long?)null, "requests"),
                _ => ((string?)null, (long?)null, (string?)null)
            };
            if (name is null) continue;
            var capacityValue = Get(row, "usage");
            var currentValue = Get(row, "currentValue");
            var remainingValue = Get(row, "remaining");
            var capacity = Number(capacityValue);
            var current = Number(currentValue);
            var remaining = Number(remainingValue);
            var percent = Number(Get(row, "percentage"));
            var totalUnits = Whole(capacityValue);
            var usedUnits = Whole(currentValue);
            if (!current.HasValue && capacity is >= 0 && remaining >= 0 && remaining <= capacity)
            {
                current = capacity - remaining;
                if (totalUnits is { } total && Whole(remainingValue) is { } left && left <= total) usedUnits = total - left;
            }
            if (!percent.HasValue && capacity is > 0 && current is >= 0) percent = current / capacity * 100;
            var reset = Integer(Get(row, "nextResetTime"));
            var resetsAt = reset is > 0 and <= 253402300799999 ? DateTimeOffset.FromUnixTimeMilliseconds(reset.Value) : (DateTimeOffset?)null;
            if (percent is null && resetsAt is null && totalUnits is null && usedUnits is null) continue;
            var value = new ProviderQuotaWindow(name, percent is >= 0 ? Math.Min(percent.Value, 100) : null,
                resetsAt, minutes, totalUnits, usedUnits, amountUnit);
            if (!windows.TryGetValue(name, out var existing) || amountUnit == "credits" && existing.Unit == "tokens")
                windows[name] = value;
            else if (existing.Unit == amountUnit && existing != value) return Failed("unsupported");
        }
        var plan = Text(Get(data, "subscriptionStatus"));
        if (plan is not null && (!plan.StartsWith("glm_coding_plan", StringComparison.Ordinal) || plan.Length > 48 ||
            plan.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_'))) plan = null;
        return new(windows.Count > 0 ? "ok" : "unsupported", null, plan,
            windows.Values.OrderBy(window => window.WindowMinutes ?? long.MaxValue).ToArray());
    }

    static ProviderAccountReading Failed(string state) => new(state, null, null, []);
    static JsonElement Get(JsonElement node, string key) =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty(key, out var value) ? value : default;
    static string? Text(JsonElement node) => node.ValueKind == JsonValueKind.String ? node.GetString() : null;
    static long? Integer(JsonElement node) => node.ValueKind == JsonValueKind.Number && node.TryGetInt64(out var value) ? value : null;
    static double? Number(JsonElement node) => node.ValueKind == JsonValueKind.Number && node.TryGetDouble(out var value) &&
        double.IsFinite(value) && value >= 0 ? value : null;
    static long? Whole(JsonElement node) => node.ValueKind == JsonValueKind.Number && node.TryGetDecimal(out var value) &&
        value is >= 0 and <= long.MaxValue && decimal.Truncate(value) == value ? (long)value : null;
}

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Zhulong.Runtime.Providers.AI;

internal static class DeepSeekAccountClient
{
    static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        Timeout = TimeSpan.FromSeconds(8), MaxResponseContentBufferSize = 64 * 1024
    };

    internal static bool IsValidKey(string key) => key.Length is > 0 and <= 512 &&
        key.All(character => character is >= '!' and <= '~');

    internal static Task<ProviderAccountReading> ReadAsync(string key, CancellationToken token) =>
        ReadAsync(Client, key, token);

    internal static async Task<ProviderAccountReading> ReadAsync(HttpClient client, string key, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!IsValidKey(key)) return Failed("auth_rejected");
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.deepseek.com/user/balance");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
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

    static ProviderAccountReading ParseReading(JsonElement document)
    {
        var available = Get(document, "is_available").ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => (bool?)null
        };
        var infos = Get(document, "balance_infos");
        if (infos.ValueKind != JsonValueKind.Array) return Failed("unsupported");
        var balances = new List<ProviderBalance>();
        foreach (var info in infos.EnumerateArray())
        {
            var currency = Get(info, "currency");
            var code = currency.ValueKind == JsonValueKind.String ? currency.GetString() : null;
            if (code is not { Length: 3 } || code.Any(character => character is < 'A' or > 'Z')) continue;
            balances.Add(new(code, Amount(info, "total_balance"), Amount(info, "granted_balance"), Amount(info, "topped_up_balance")));
        }
        if (balances.Count == 0 && (infos.GetArrayLength() > 0 || available is null)) return Failed("unsupported");
        return new("ok", null, null, [], Wallet: new(available, balances));
    }

    static ProviderAccountReading Failed(string state) => new(state, null, null, []);
    static JsonElement Get(JsonElement node, string name) =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out var value) ? value : default;

    static decimal? Amount(JsonElement info, string name)
    {
        var value = Get(info, name);
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDecimal(out var number) => number,
            JsonValueKind.String when decimal.TryParse(value.GetString(), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var number) => number,
            _ => null
        };
    }
}

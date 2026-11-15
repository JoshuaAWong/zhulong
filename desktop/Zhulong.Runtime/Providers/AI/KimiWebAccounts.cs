using System.Text;
using System.Text.Json;

namespace Zhulong.Runtime.Providers.AI;

public sealed record KimiWebAccount(string Id, string Label, bool Domestic);

public static class KimiWebAccounts
{
    const string Prefix = "kimi-web-";
    static readonly KimiWebClient Client = new();
    static readonly SemaphoreSlim Mutation = new(1, 1);
    static readonly Dictionary<(string Directory, string Id), KimiWebTokens> ActiveTokens = new();
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Task<KimiDeviceAuthorization> StartAsync(bool domestic, CancellationToken token) =>
        Client.StartAsync(domestic, token);

    public static IReadOnlyList<KimiWebAccount> Load(string directory) =>
        LoadEntries(directory).Select(entry => entry.View).ToArray();

    public static Task<KimiWebAccount> BindAsync(string directory, KimiDeviceAuthorization flow,
        string? label, string? replaceId, CancellationToken token) =>
        BindAsync(Client, directory, flow, label, replaceId, token);

    internal static async Task<KimiWebAccount> BindAsync(KimiWebClient client, string directory,
        KimiDeviceAuthorization flow, string? label, string? replaceId, CancellationToken token)
    {
        var credentials = await client.CompleteAsync(flow, token).ConfigureAwait(false);
        var identity = await client.ReadIdentityAsync(flow.Domestic, credentials.AccessToken, token).ConfigureAwait(false);
        await Mutation.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var entries = LoadEntries(directory).ToList();
            var index = replaceId is null ? -1 : entries.FindIndex(entry => entry.Id == replaceId);
            if (replaceId is not null && index < 0)
                throw new KimiWebAuthException("not_configured");
            if (entries.Any(entry => entry.Id != replaceId && entry.Identity == identity.Identity))
                throw new KimiWebAuthException("duplicate_account");
            if (index >= 0 && entries[index].Identity != identity.Identity)
                throw new KimiWebAuthException("account_mismatch");

            var id = replaceId ?? Prefix + Guid.NewGuid().ToString("N");
            var name = label?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                name = index >= 0 ? entries[index].Label : $"Kimi 账号 {entries.Count + 1}";
                for (var number = entries.Count + 2; index < 0 && entries.Any(entry => entry.Label == name); number++)
                    name = $"Kimi 账号 {number}";
            }
            var updated = new Entry(id, name, flow.Domestic, identity.Identity, identity.PlanType, Guid.NewGuid());
            if (index < 0) entries.Add(updated);
            else entries[index] = updated;
            SaveBinding(directory, id, entries, credentials.RefreshToken);
            ActiveTokens[(directory, id)] = credentials;
            return updated.View;
        }
        finally { Mutation.Release(); }
    }

    public static async Task RemoveAsync(string directory, string id, CancellationToken token)
    {
        await Mutation.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var entries = LoadEntries(directory);
            if (!entries.Any(entry => entry.Id == id)) return;
            SaveBinding(directory, id, entries.Where(entry => entry.Id != id).ToArray(), null);
            ActiveTokens.Remove((directory, id));
        }
        finally { Mutation.Release(); }
    }

    internal static bool IsAccountId(string? id) =>
        id is not null && id.StartsWith(Prefix, StringComparison.Ordinal) &&
        Guid.TryParseExact(id.AsSpan(Prefix.Length), "N", out _);

    internal static Entry[] LoadEntries(string directory)
    {
        var path = AccountsPath(directory);
        if (!File.Exists(path)) return [];
        var entries = JsonSerializer.Deserialize<Entry[]>(File.ReadAllText(path, Encoding.UTF8)) ?? [];
        if (entries.Any(entry => entry is null || !IsAccountId(entry.Id)))
            throw new JsonException("Kimi 账号列表包含无效标识。");
        return entries;
    }

    internal static Task<(ProviderAccountReading Reading, Guid? Revision)> ReadAsync(string directory, string id,
        CancellationToken token) => ReadAsync(Client, directory, id, token);

    internal static async Task<(ProviderAccountReading Reading, Guid? Revision)> ReadAsync(KimiWebClient client,
        string directory, string id, CancellationToken token)
    {
        await Mutation.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var account = LoadEntries(directory).FirstOrDefault(entry => entry.Id == id);
            if (account is null) return (new("not_configured", null, null, []), null);
            var key = (directory, id);
            var fromCache = ActiveTokens.TryGetValue(key, out var cached) &&
                cached.ExpiresAt > DateTimeOffset.UtcNow.AddSeconds(30);
            try
            {
                var credentials = fromCache ? cached! : await RenewAsync(client, directory, account, token).ConfigureAwait(false);
                IReadOnlyList<ProviderQuotaWindow> windows;
                try { windows = await client.ReadQuotaAsync(account.Domestic, credentials.AccessToken, token).ConfigureAwait(false); }
                catch (KimiWebAuthException error) when (fromCache && error.State == "auth_expired")
                {
                    ActiveTokens.Remove(key);
                    credentials = await RenewAsync(client, directory, account, token).ConfigureAwait(false);
                    windows = await client.ReadQuotaAsync(account.Domestic, credentials.AccessToken, token).ConfigureAwait(false);
                }
                return (new(windows.Count == 0 ? "unsupported" : "ok", account.Identity, account.PlanType, windows), account.Revision);
            }
            catch (KimiWebAuthException error)
            {
                if (error.State == "auth_expired") ActiveTokens.Remove(key);
                return (new(error.State, account.Identity, account.PlanType, []), account.Revision);
            }
        }
        finally { Mutation.Release(); }
    }

    static async Task<KimiWebTokens> RenewAsync(KimiWebClient client, string directory, Entry account, CancellationToken token)
    {
        var previous = AiAccountProfiles.ReadKey(directory, account.Id, out _);
        if (string.IsNullOrEmpty(previous)) throw new KimiWebAuthException("auth_expired");
        var credentials = await client.RefreshAsync(account.Domestic, previous, token).ConfigureAwait(false);
        if (credentials.RefreshToken != previous) AiAccountProfiles.SetKey(directory, account.Id, credentials.RefreshToken);
        ActiveTokens[(directory, account.Id)] = credentials;
        return credentials;
    }

    static void SaveBinding(string directory, string id, IReadOnlyList<Entry> entries, string? refreshToken)
    {
        var path = AccountsPath(directory);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(entries, Json), Encoding.UTF8);
        var previous = AiAccountProfiles.ReadKey(directory, id, out _);
        AiAccountProfiles.SetKey(directory, id, refreshToken);
        try { File.Move(temporary, path, true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            AiAccountProfiles.SetKey(directory, id, previous);
            throw;
        }
    }

    static string AccountsPath(string directory) => Path.Combine(directory, "kimi-web-accounts.json");

    internal sealed record Entry(string Id, string Label, bool Domestic, string Identity, string? PlanType, Guid Revision)
    {
        internal KimiWebAccount View => new(Id, Label, Domestic);
    }
}

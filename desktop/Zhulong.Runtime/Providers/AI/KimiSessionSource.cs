using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Zhulong.Runtime.Providers.AI;

internal sealed class KimiSessionSource(string home) : IDisposable
{
    static readonly ParsedLine Empty = new([], null);
    static readonly string[] CountNames = ["inputOther", "inputCacheRead", "inputCacheCreation", "output"];
    readonly string sessions = Path.Combine(home, "sessions");
    readonly object changesGate = new();
    readonly HashSet<string> changes = new(StringComparer.OrdinalIgnoreCase);
    IncrementalSources? sources;
    FileSystemWatcher? watcher;
    string? active;

    internal ScanResult ScanBatch(UsageStore store, CancellationToken token)
    {
        const int budget = 4 * 1024 * 1024;
        int visited = 0, files = 0, lines = 0, added = 0;
        long bytes = 0;
        StartWatcher();
        var scanner = new CodexScanner(store);
        int liveFiles = 0;
        while (bytes < budget && files < 48 && visited < 256)
        {
            sources ??= new([sessions], "wire.jsonl");
            if (active is null && liveFiles < 12 && TakeChanged() is { } changed)
            {
                active = changed;
                liveFiles++;
            }
            if (active is null)
            {
                active = sources.Next(ref visited, 256, token, out var finished);
                if (finished) { sources.Dispose(); sources = null; return new(bytes, lines, added, false); }
                if (active is null) break;
            }
            var path = active;
            active = null;
            if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) { files++; continue; }
            var sessionId = SessionId(path);
            var result = scanner.ScanFile(path, budget - (int)bytes,
                (line, state) => Parse(line, state, sessionId), "usage.record"u8, token);
            bytes += result.BytesRead;
            lines += result.LinesRead;
            added += result.EventsAdded;
            files++;
            if (result.HasMore) active = path;
        }
        return new(bytes, lines, added, true);
    }

    static string SessionId(string path)
    {
        var folder = Path.GetDirectoryName(path)!;
        var parent = Path.GetDirectoryName(folder);
        if (Path.GetFileName(parent) == "agents") folder = Path.GetDirectoryName(parent!)!;
        return Path.GetFileName(folder);
    }

    internal static ParsedLine Parse(string line, ParserState state, string sessionId)
    {
        using var document = JsonDocument.Parse(line);
        var item = document.RootElement;
        if (String(item, "type") != "usage.record" || String(item, "agentId") is not { Length: > 0 } agent ||
            !item.TryGetProperty("time", out var time) || time.ValueKind != JsonValueKind.Number || !time.TryGetInt64(out var stamp) ||
            stamp is <= 0 or > 253402300799999 || !item.TryGetProperty("usage", out var values) ||
            values.ValueKind != JsonValueKind.Object) return Empty;
        var counts = new long?[4];
        var known = false;
        for (var i = 0; i < CountNames.Length; i++)
        {
            if (!values.TryGetProperty(CountNames[i], out var value)) continue;
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var count) || count is < 0 or > int.MaxValue) return Empty;
            counts[i] = count;
            known = true;
        }
        if (!known) return Empty;
        var input = counts[0].GetValueOrDefault() + counts[1].GetValueOrDefault() + counts[2].GetValueOrDefault();
        var output = counts[3].GetValueOrDefault();
        var model = String(item, "model") ?? "unknown";
        var identity = JsonSerializer.Serialize(new { stamp, agent, model, scope = String(item, "usageScope"), counts });
        var id = "kimi:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        var usage = new TokenUsage(input, counts[1].GetValueOrDefault(), counts[2].GetValueOrDefault(), output, 0, input + output);
        state.SessionId = sessionId;
        state.SawTokenCount = true;
        return new([new(id, sessionId, null, model, DateTimeOffset.FromUnixTimeMilliseconds(stamp), usage, "kimi_usage_record")], null);
    }

    static string? String(JsonElement item, string property) => item.ValueKind == JsonValueKind.Object &&
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    void StartWatcher()
    {
        if (watcher is not null || !Directory.Exists(sessions)) return;
        var candidate = new FileSystemWatcher(sessions, "wire.jsonl")
        { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite };
        candidate.Changed += Changed;
        candidate.Created += Changed;
        candidate.Renamed += Changed;
        try { candidate.EnableRaisingEvents = true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { candidate.Dispose(); return; }
        watcher = candidate;
    }

    void Changed(object sender, FileSystemEventArgs args)
    {
        lock (changesGate) if (changes.Count < 128) changes.Add(args.FullPath);
    }

    string? TakeChanged()
    {
        lock (changesGate)
        {
            var next = changes.FirstOrDefault();
            if (next is not null) changes.Remove(next);
            return next;
        }
    }

    public void Dispose() { watcher?.Dispose(); sources?.Dispose(); }
}

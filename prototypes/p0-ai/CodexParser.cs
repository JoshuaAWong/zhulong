using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Zhulong.P0.AI;

// 累计流及压缩去重参考 TokenTracker 68c2258，MIT，版权见 THIRD_PARTY_NOTICES.md。
// 这里只留下用量与关联元数据，不保存提示词、回答或工具参数。
public static class CodexParser
{
    public static ParsedLine Parse(string line, ParserState state)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        var type = Text(root, "type");
        var payload = Child(root, "payload");
        var timestamp = Timestamp(Text(root, "timestamp"));
        var events = new List<UsageEvent>(2);
        QuotaSnapshot? quota = null;

        // 压缩标记必须紧跟用量记录；中间出现别的完整行就不能推断为压缩。
        if (state.CompactionCandidate is not null && !state.CandidateCompacted && type != "token_usage_record")
        {
            state.CandidateCompacted = type == "compacted";
            if (!state.CandidateCompacted) state.CompactionCandidate = null;
        }
        if (type == "session_meta")
        {
            state.SessionId = Text(payload, "id") ?? state.SessionId;
            state.ParentSessionId = ParentId(payload) ?? state.ParentSessionId;
        }
        else if (type == "turn_context")
        {
            state.Model = Text(payload, "model") ?? state.Model;
        }
        else if (type == "token_usage_record")
        {
            var usage = ReadUsage(Child(payload, "usage"));
            var responseId = Text(payload, "response_id");
            state.CompactionCandidate = state.SawTokenCount && usage is not null && timestamp.HasValue && responseId is not null
                ? MakeEvent("codex:response:" + responseId, state, timestamp.Value, usage, "compaction") : null;
            state.CandidateCompacted = false;
            if (!state.SawTokenCount)
            {
                if (state.UnmatchedRecord) state.RecordOnly = true;
                state.UnmatchedRecord = true;
            }
        }
        else if (type == "event_msg")
        {
            var eventType = Text(payload, "type");
            if (eventType == "token_count")
            {
                if (timestamp.HasValue) quota = ReadQuota(Child(payload, "rate_limits"), timestamp.Value);
                var info = Child(payload, "info");
                if (info.ValueKind == JsonValueKind.Object)
                {
                    var last = ReadUsage(Child(info, "last_token_usage"));
                    var total = ReadUsage(Child(info, "total_token_usage"));
                    var previous = state.Baselines.LastOrDefault();
                    if (timestamp.HasValue && state.CandidateCompacted && state.CompactionCandidate is { } compaction &&
                        previous is not null && previous == total)
                        events.Add(compaction);
                    state.CompactionCandidate = null;
                    state.CandidateCompacted = false;
                    state.SawTokenCount = true;
                    state.UnmatchedRecord = false;
                    state.RecordOnly = false;
                    if (!IsResetSentinel(last, total))
                    {
                        var delta = ConsumeDelta(state, last, total);
                        if (timestamp.HasValue && delta is not null && delta.Total > 0)
                        {
                            var identity = string.Join("|", timestamp.Value.ToUniversalTime().ToString("O"),
                                state.Model, Signature(last), Signature(total), Signature(delta));
                            var id = "codex:usage:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
                            events.Add(MakeEvent(id, state, timestamp.Value, delta, "token_count"));
                        }
                    }
                }
            }
            else if (eventType is "task_complete" or "turn_aborted")
            {
                if (!state.SawTokenCount && state.UnmatchedRecord) state.RecordOnly = true;
                state.UnmatchedRecord = false;
            }
        }
        return new ParsedLine(events, quota);
    }

    private static UsageEvent MakeEvent(string id, ParserState state, DateTimeOffset timestamp,
        TokenUsage usage, string kind) => new(id, state.SessionId ?? "unknown", state.ParentSessionId,
            state.Model, timestamp.ToUniversalTime(), usage, kind);

    // 无稳定计数流 ID 时，用 total-last 追踪各流的前驱；最多保留 32 条基线。
    private static TokenUsage? ConsumeDelta(ParserState state, TokenUsage? last, TokenUsage? total)
    {
        if (total is null) return last;
        var active = state.Baselines.LastOrDefault();
        var index = state.Baselines.IndexOf(total);
        if (index >= 0)
        {
            if (active != total) state.SawInterleaved = true;
            Touch(state, index, total);
            return null;
        }
        if (last is not null)
        {
            var expected = Subtract(total, last, false);
            index = expected is null ? -1 : state.Baselines.IndexOf(expected);
            if (index >= 0)
            {
                if (active != expected) state.SawInterleaved = true;
                Touch(state, index, total);
                return last;
            }
            if (expected is not null)
            {
                if (active is not null) state.SawDivergentCumulative = true;
                Touch(state, -1, total);
                return last;
            }
        }
        if (active is not null && total.Total >= active.Total)
        {
            var delta = Subtract(total, active, true)!;
            if (last is null || delta.Total <= last.Total)
            {
                Touch(state, state.Baselines.IndexOf(active), total);
                return delta;
            }
            state.SawDivergentCumulative = true;
        }
        Touch(state, -1, total);
        return last ?? total;
    }

    private static void Touch(ParserState state, int index, TokenUsage total)
    {
        if (index >= 0) state.Baselines.RemoveAt(index);
        state.Baselines.Add(total);
        if (state.Baselines.Count > 32) state.Baselines.RemoveAt(0);
    }

    private static TokenUsage? Subtract(TokenUsage a, TokenUsage b, bool clamp)
    {
        if (!clamp && (a.Input < b.Input || a.CachedInput < b.CachedInput || a.CacheWrite < b.CacheWrite ||
            a.Output < b.Output || a.Reasoning < b.Reasoning || a.Total < b.Total)) return null;
        return new(Math.Max(0, a.Input - b.Input), Math.Max(0, a.CachedInput - b.CachedInput),
            Math.Max(0, a.CacheWrite - b.CacheWrite), Math.Max(0, a.Output - b.Output),
            Math.Max(0, a.Reasoning - b.Reasoning), Math.Max(0, a.Total - b.Total));
    }

    private static bool IsResetSentinel(TokenUsage? last, TokenUsage? total) => last is { Total: > 0 } &&
        total is { Total: 0 } && last.Input == 0 && last.CachedInput == 0 && last.CacheWrite == 0 &&
        last.Output == 0 && last.Reasoning == 0 && total.Input == 0 && total.CachedInput == 0 &&
        total.CacheWrite == 0 && total.Output == 0 && total.Reasoning == 0;

    private static TokenUsage? ReadUsage(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return null;
        var input = Integer(value, "input_tokens");
        var cached = Integer(value, "cached_input_tokens");
        var write = Integer(value, "cache_creation_input_tokens") ?? Integer(value, "cache_write_input_tokens");
        var output = Integer(value, "output_tokens");
        var reasoning = Integer(value, "reasoning_output_tokens");
        var total = Integer(value, "total_tokens");
        if (input is null && cached is null && write is null && output is null && reasoning is null && total is null) return null;
        return new(input ?? 0, cached ?? 0, write ?? 0, output ?? 0, reasoning ?? 0,
            total ?? checked((input ?? 0) + (output ?? 0)));
    }

    private static QuotaSnapshot? ReadQuota(JsonElement value, DateTimeOffset timestamp)
    {
        if (value.ValueKind != JsonValueKind.Object) return null;
        return new(Text(value, "limit_id") ?? "codex", Text(value, "plan_type"), timestamp.ToUniversalTime(),
            ReadWindow(Child(value, "primary"), "primary"), ReadWindow(Child(value, "secondary"), "secondary"));
    }

    private static QuotaWindow? ReadWindow(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object) return null;
        var percent = Child(value, "used_percent");
        double? used = percent.TryGetDoubleSafe();
        var reset = Integer(value, "resets_at");
        DateTimeOffset? resetsAt = reset is >= -62135596800 and <= 253402300799
            ? DateTimeOffset.FromUnixTimeSeconds(reset.Value) : null;
        return new(name, used, Integer(value, "window_minutes"), resetsAt);
    }

    private static string? ParentId(JsonElement value)
    {
        var direct = Text(value, "forked_from_id");
        var agent = Child(Child(value, "source"), "subagent");
        return direct ?? Text(agent, "parent_thread_id") ??
            Text(Child(agent, "thread_spawn"), "parent_thread_id") ?? Text(Child(agent, "spawn"), "parent_thread_id");
    }

    private static string Signature(TokenUsage? value) => value is null ? "null" :
        $"{value.Input}:{value.CachedInput}:{value.CacheWrite}:{value.Output}:{value.Reasoning}:{value.Total}";

    private static JsonElement Child(JsonElement value, string key) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var child) ? child : default;

    private static string? Text(JsonElement value, string key)
    {
        var child = Child(value, key);
        return child.ValueKind == JsonValueKind.String ? child.GetString() : null;
    }

    private static long? Integer(JsonElement value, string key)
    {
        var child = Child(value, key);
        return child.ValueKind == JsonValueKind.Number && child.TryGetInt64(out var number) && number >= 0 ? number : null;
    }

    private static double? TryGetDoubleSafe(this JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number) ? number : null;

    private static DateTimeOffset? Timestamp(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time) ? time : null;
}

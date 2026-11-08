namespace Zhulong.Runtime.Providers.AI;

// 输入含缓存、输出含推理；子集只用于拆分展示，不能再加到总量。
public sealed record TokenUsage(long Input, long CachedInput, long CacheWrite,
    long Output, long Reasoning, long Total);

public sealed record UsageEvent(string EventId, string SessionId, string? ParentSessionId,
    string Model, DateTimeOffset Timestamp, TokenUsage Usage, string Kind);

public sealed record QuotaWindow(string Name, double? UsedPercent, long? WindowMinutes,
    DateTimeOffset? ResetsAt);

public sealed record QuotaSnapshot(string LimitId, string? PlanType, DateTimeOffset ObservedAt,
    QuotaWindow? Primary, QuotaWindow? Secondary);

public sealed record ParsedLine(IReadOnlyList<UsageEvent> Usage, QuotaSnapshot? Quota);

// 与游标一起提交；跨批次、重启后继续同一条累计计数流。
public sealed class ParserState
{
    public string? SessionId { get; set; }
    public string? ParentSessionId { get; set; }
    public string Model { get; set; } = "unknown";
    public List<TokenUsage> Baselines { get; set; } = [];
    public bool SawDivergentCumulative { get; set; }
    public bool SawInterleaved { get; set; }
    public bool SawTokenCount { get; set; }
    public bool UnmatchedRecord { get; set; }
    public bool RecordOnly { get; set; }
    public UsageEvent? CompactionCandidate { get; set; }
    public bool CandidateCompacted { get; set; }
}

namespace Zhulong.Core;

public sealed record ProcessRow(int Pid, string Name, double MemoryGb, double? CpuPercent,
    double? CommitGb = null, double? ReadMbS = null, double? WriteMbS = null,
    long? CreatedTicks = null, string? ExecutablePath = null);
public sealed record MetricValue(string Name, double? Value, string Unit,
    string? Label = null, string State = "ok", DateTimeOffset? SampledAt = null);
public sealed record SystemSnapshot(DateTimeOffset Timestamp, double? CommitPercent,
    double? CommitUsedGb, double? CommitLimitGb, double? MemoryPercent, double? MemoryUsedGb,
    double? MemoryTotalGb, double? CpuPercent, List<ProcessRow> Processes, string? Error = null,
    Dictionary<string, MetricValue>? Metrics = null, List<ProcessRow>? ProcessMetrics = null);
public sealed record RuntimeEvent(DateTimeOffset Timestamp, string Title, string Text, string Level,
    string Kind = "observation", string? Target = null);
public sealed record AlertDecision(string Rule, string Title, string Text, string Level,
    string? ProcessName = null, int? Pid = null, long? CreatedTicks = null, string? SuggestedAction = null, string? Target = null);
public sealed record ActionResult(string Status, string Action, string Target, string? Detail = null);
public sealed record ProcessPolicy(string Name, double MaxCommitGb = 15, int MinAgeSeconds = 60,
    int MaxActionsPerTenMinutes = 2, string? PathContains = null);
public sealed record TrimPolicy(string Name, double MinWorkingSetGb = 2, int MinAgeSeconds = 300,
    int MaxTrimsPerTenMinutes = 1, string? PathContains = null);
public sealed record AlertPolicy(double CommitEnter = 85, double CommitExit = 80, int CommitSeconds = 90,
    int CooldownSeconds = 600, double SingleCoreEnter = 95, double SingleCoreAverageBelow = 70, int SingleCoreSeconds = 90);
public sealed record ServicePolicy(string Name, IReadOnlyList<string> TriggerProcesses, int MaxRestartsPerHour = 1);
public sealed record ThrottlePolicy(string Name, int Cores = 2, string Priority = "below_normal",
    string Mode = "spread", IReadOnlyList<int>? CpuIndices = null);
public sealed class RuntimeSettings
{
    public AlertPolicy Alerts { get; set; } = new();
    public double TrimPressurePercent {get;set;} = 60;
    public int SampleSeconds { get; set; } = 30;
    public int RetentionDays { get; set; } = 30;
    public bool PingEnabled { get; set; } = true;
    public string PingHost { get; set; } = "223.5.5.5";
    public bool AutomaticActions { get; set; }
    public bool Notifications { get; set; } = true;
    public bool ThrottleEnabled { get; set; }
    public bool MemoryTrimEnabled { get; set; }
    public List<ProcessPolicy> WatchProcesses { get; set; } = [new("HYPHelper.exe", PathContains: "miHoYo Launcher")];
    public List<ThrottlePolicy> ThrottleRules { get; set; } = [new("ToDesk.exe"),new("AweSun.exe"),new("sunloginclient.exe")];
    public List<string> AutoKill { get; set; } = [];
    public List<string> RestartServices { get; set; } = [];
    public List<ServicePolicy> RestartServiceRules { get; set; } = [];
    public List<TrimPolicy> TrimProcesses { get; set; } = [new("HYPHelper.exe",MinAgeSeconds:300,PathContains:"miHoYo Launcher")];
}

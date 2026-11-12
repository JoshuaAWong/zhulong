namespace Zhulong.Runtime.Providers.AI;

internal sealed record ProviderQuotaWindow(string Name, double? UsedPercent, DateTimeOffset? ResetsAt,
    long? WindowMinutes = null, long? TotalUnits = null, long? UsedUnits = null, string? Unit = null);

internal sealed record ProviderAccountReading(string State, string? Identity, string? PlanType,
    IReadOnlyList<ProviderQuotaWindow> Windows, long? AccountTokens = null);

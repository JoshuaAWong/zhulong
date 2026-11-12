namespace Zhulong.Runtime.Providers.AI;

internal sealed record ProviderQuotaWindow(string Name, double? UsedPercent, DateTimeOffset? ResetsAt,
    long? WindowMinutes = null, long? TotalUnits = null, long? UsedUnits = null, string? Unit = null);

internal sealed record ProviderBalance(string Currency, decimal? Total, decimal? Granted, decimal? ToppedUp);
internal sealed record ProviderWallet(bool? IsAvailable, IReadOnlyList<ProviderBalance> Balances);

internal sealed record ProviderAccountReading(string State, string? Identity, string? PlanType,
    IReadOnlyList<ProviderQuotaWindow> Windows, long? AccountTokens = null, ProviderWallet? Wallet = null);

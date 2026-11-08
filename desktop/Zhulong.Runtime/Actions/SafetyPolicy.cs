namespace Zhulong.Runtime.Actions;

public static class SafetyPolicy
{
    static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "registry", "memory compression", "system idle process", "idle", "explorer", "dwm", "csrss",
        "winlogon", "lsass", "services", "svchost", "pythonw", "python", "zenlesszonezero", "smss", "wininit",
        "fontdrvhost", "zhulong", "qqpctray", "qqpcrtp", "qqpcmgr", "360tray", "360safe", "zhudongfangyu",
        "360sd", "hipsdaemon", "usysdiag", "wsctrl", "msmpeng", "msascuil", "nissrv", "securityhealthservice",
        "securityhealthsystray", "sense", "smartscreen"
    };
    public static string Normalize(string name)
    {
        var value = Path.GetFileName(name).Trim().ToLowerInvariant();
        return value.EndsWith(".exe", StringComparison.Ordinal) ? value[..^4] : value;
    }
    public static bool NameBlocked(string name) => Protected.Contains(Normalize(name));
    public static string? ProcessBlocked(ProcessIdentity value, int currentPid) =>
        value.Pid == currentPid ? "self" : NameBlocked(value.Name) ? "protected_process" : value.ProtectionReason;
    public static bool ServiceBlocked(string name) => NameBlocked(name) || name.Trim().ToLowerInvariant() is
        "windefend" or "wdnissvc" or "wscsvc" or "rpcss" or "dcomlaunch" or "plugplay" or "power" or "lsm" or "samss";
}

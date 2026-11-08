using System.Text.Json;
using Zhulong.Core;
namespace Zhulong.Runtime.Storage;

public sealed class SettingsStore(string directory)
{
    public string PathName => Path.Combine(directory, "settings.json");
    public RuntimeSettings Load()
    {
        if (!File.Exists(PathName)) return new();
        var value = JsonSerializer.Deserialize<RuntimeSettings>(File.ReadAllText(PathName, System.Text.Encoding.UTF8),new JsonSerializerOptions{UnmappedMemberHandling=System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow})
            ?? throw new InvalidDataException("新版设置内容为空。");
        Validate(value);
        return value;
    }
    public void Save(RuntimeSettings value)
    {
        Validate(value);
        Directory.CreateDirectory(directory);
        var temporary = PathName + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, value, new JsonSerializerOptions { WriteIndented = true });
            stream.Flush(true);
        }
        File.Move(temporary, PathName, true);
    }
    public static void Validate(RuntimeSettings value)
    {
        if (value.SampleSeconds is < 5 or > 300 || value.RetentionDays is < 1 or > 365)
            throw new ArgumentException("采样间隔应为5–300秒，保留天数应为1–365天。");
        if (value.WatchProcesses is null || value.ThrottleRules is null || value.AutoKill is null || value.RestartServices is null || value.RestartServiceRules is null || value.TrimProcesses is null)
            throw new ArgumentException("规则列表不能为空值。");
        if(value.WatchProcesses.Any(p=>p is null)||value.TrimProcesses.Any(p=>p is null)||value.ThrottleRules.Any(p=>p is null)||value.RestartServiceRules.Any(p=>p is null||string.IsNullOrWhiteSpace(p.Name)||p.TriggerProcesses is null||p.TriggerProcesses.Count==0||p.TriggerProcesses.Any(string.IsNullOrWhiteSpace)||p.MaxRestartsPerHour is <1 or >10)||value.AutoKill.Any(string.IsNullOrWhiteSpace)||value.RestartServices.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("规则条目、名称及服务触发列表不能为空；服务每小时次数应为1–10。");
        if (value.WatchProcesses.Count > 50 || value.ThrottleRules.Count > 50 || value.AutoKill.Count > 50 || value.RestartServices.Count > 50 || value.RestartServiceRules.Count > 50 || value.TrimProcesses.Count > 50)
            throw new ArgumentException("每类规则最多50项。");
        if (value.WatchProcesses.Any(p => string.IsNullOrWhiteSpace(p.Name) || p.MaxCommitGb <= 0 || !double.IsFinite(p.MaxCommitGb) || p.MinAgeSeconds < 60 || p.MaxActionsPerTenMinutes is < 1 or > 10))
            throw new ArgumentException("进程策略名称、水位、年龄或频次无效。");
        if(value.TrimProcesses.Any(p=>string.IsNullOrWhiteSpace(p.Name)||!double.IsFinite(p.MinWorkingSetGb)||p.MinWorkingSetGb<2||p.MinAgeSeconds<300||p.MaxTrimsPerTenMinutes is <1 or >10))
            throw new ArgumentException("整理策略工作集至少2GB、存活至少300秒，10分钟频次应为1–10。");
        if(value.RestartServices.Concat(value.RestartServiceRules.Select(p=>p.Name)).Distinct(StringComparer.OrdinalIgnoreCase).Count()>50)
            throw new ArgumentException("服务重启白名单合计最多50个服务。");
        if(value.RestartServiceRules.SelectMany(p=>p.TriggerProcesses).Any(Zhulong.Runtime.Actions.SafetyPolicy.NameBlocked))
            throw new ArgumentException("服务触发进程不支持系统/受保护名称；共享svchost须先做服务归属，不能按进程名盲判。");
        if(value.RestartServices.Concat(value.RestartServiceRules.Select(p=>p.Name)).Any(Zhulong.Runtime.Actions.SafetyPolicy.ServiceBlocked))
            throw new ArgumentException("安全服务与系统关键服务不能进入重启白名单。");
        var alerts=value.Alerts;
        if(alerts is null||!double.IsFinite(alerts.CommitEnter)||!double.IsFinite(alerts.CommitExit)||alerts.CommitExit<0||alerts.CommitEnter>100||alerts.CommitEnter<=alerts.CommitExit||alerts.CommitSeconds<5||alerts.CooldownSeconds<60||!double.IsFinite(alerts.SingleCoreEnter)||alerts.SingleCoreEnter is <1 or >100||!double.IsFinite(alerts.SingleCoreAverageBelow)||alerts.SingleCoreAverageBelow is <1 or >100||alerts.SingleCoreSeconds<5||!double.IsFinite(value.TrimPressurePercent)||value.TrimPressurePercent is <1 or >100)
            throw new ArgumentException("告警水位、迟滞、持续时长、冷却或整理压力门槛无效。");
        if(value.WatchProcesses.Select(p=>p.Name).Concat(value.TrimProcesses.Select(p=>p.Name)).Concat(value.AutoKill).Concat(value.ThrottleRules.Select(p=>p.Name)).Any(Zhulong.Runtime.Actions.SafetyPolicy.NameBlocked))
            throw new ArgumentException("安全软件、系统关键进程和烛龙自身不能进入处置或限流白名单。");
        if (value.ThrottleRules.Any(p => string.IsNullOrWhiteSpace(p.Name) || p.Cores < 1 || p.Mode is not ("spread" or "tail" or "explicit") || p.Mode=="explicit"&&(p.CpuIndices is null||p.CpuIndices.Count is <1 or >64||p.CpuIndices.Any(i=>i is <0 or >63)) || p.Priority is not ("below_normal" or "normal")))
            throw new ArgumentException("限流策略无效。");
        if (value.PingEnabled && !System.Net.IPAddress.TryParse(value.PingHost, out _))
            throw new ArgumentException("公网延迟目标须填写IP地址。");
    }
}

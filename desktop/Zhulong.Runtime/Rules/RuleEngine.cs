using Zhulong.Core;
using Zhulong.Runtime.Actions;

namespace Zhulong.Runtime.Rules;

// 窗口和冷却只使用调用方传入的单调秒数；快照墙钟只用于记录展示。
public sealed class RuleEngine(RuntimeSettings settings)
{
    sealed class Window { public double? Since; public double Next; public bool Latched; }
    sealed record Point(double At, double Commit);
    readonly Dictionary<string, Window> windows = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, List<Point>> histories = [];
    readonly Dictionary<string, double> redlineNotified = [];
    readonly Dictionary<string, double> lastSeen = [];

    public IReadOnlyList<AlertDecision> Evaluate(SystemSnapshot snapshot, double monotonicSeconds)
    {
        var output = new List<AlertDecision>();
        if (Check("commit_high", snapshot.CommitPercent >= settings.Alerts.CommitEnter, snapshot.CommitPercent, settings.Alerts.CommitSeconds, monotonicSeconds, true))
            output.Add(new("commit_high", "提交内存水位持续偏高", $"提交水位持续{settings.Alerts.CommitSeconds}秒；进入{settings.Alerts.CommitEnter:F0}%，回落至{settings.Alerts.CommitExit:F0}%解除。", "warning"));
        var maximum = Metric(snapshot, "cpu_max_core", "cpu.cpu_max_core", "cpu.max_core");
        bool? singleCore = maximum.HasValue && snapshot.CpuPercent.HasValue ? maximum >= settings.Alerts.SingleCoreEnter && snapshot.CpuPercent < settings.Alerts.SingleCoreAverageBelow : null;
        if (Check("single_core_hot", singleCore, maximum, settings.Alerts.SingleCoreSeconds, monotonicSeconds))
            output.Add(new("single_core_hot", "单核持续饱和", $"最忙核心达到{settings.Alerts.SingleCoreEnter:F0}%，整机低于{settings.Alerts.SingleCoreAverageBelow:F0}%，持续{settings.Alerts.SingleCoreSeconds}秒。", "warning"));

        var rows = (snapshot.ProcessMetrics ?? snapshot.Processes).Where(row => !SafetyPolicy.NameBlocked(row.Name)).ToArray();
        var anomalies = new List<ProcessRow>();
        foreach (var row in rows)
        {
            if (row.CommitGb is not { } current) continue;
            var key = row.Pid + ":" + row.CreatedTicks;
            lastSeen[key] = monotonicSeconds;
            if (row.CreatedTicks.HasValue)
            {
                if (!histories.TryGetValue(key, out var points)) histories[key] = points = [];
                points.RemoveAll(point => monotonicSeconds - point.At > 1800);
                points.Add(new Point(monotonicSeconds, current));
                // 不同PID/创建时间不拼接；固定窗口内最多保留一秒一个点。
                if (points.Count > 1801) points.RemoveAt(0);
            }
            var isRedline = current > 20;
            var suspect = histories.TryGetValue(key, out var history) && history.Count >= 2 && current > 8 &&
                current - history[0].Commit > 4 && current - history[0].Commit > history[0].Commit * .5;
            if (!isRedline) redlineNotified.Remove(key);
            if (!isRedline && !suspect) continue;
            anomalies.Add(row);
            if (isRedline && redlineNotified.TryGetValue(key, out var previous) && current <= previous + 1) continue;
            if (!Check("leak:" + key, true, current, 0, monotonicSeconds)) continue;
            if (isRedline) redlineNotified[key] = current;
            var auto = settings.AutoKill.Any(name => SafetyPolicy.Normalize(name) == SafetyPolicy.Normalize(row.Name));
            output.Add(new("anomaly_leak", "进程内存异常", isRedline ? $"{row.Name}当前提交内存{current:F1}GB，超过20GB红线。" :
                $"{row.Name}在30分钟观察窗内增长超过4GB和50%，当前{current:F1}GB，疑似泄漏。", "warning",
                row.Name, row.Pid, row.CreatedTicks, auto ? "kill_process" : null, row.Name));
        }
        foreach (var policy in settings.WatchProcesses)
        {
            var row = rows.Where(row => SafetyPolicy.Normalize(row.Name) == SafetyPolicy.Normalize(policy.Name))
                .OrderByDescending(row => row.CommitGb).FirstOrDefault();
            var high = row?.CommitGb >= policy.MaxCommitGb;
            if (Check("watch:" + policy.Name, high, row?.CommitGb, 0, monotonicSeconds) && row is not null)
                output.Add(new("watch_commit", "白名单进程超过水位", $"{row.Name}提交内存达到{row.CommitGb:F1}GB，阈值{policy.MaxCommitGb:F1}GB。",
                    "warning", row.Name, row.Pid, row.CreatedTicks, "kill_process", policy.Name));
        }
        foreach (var policy in settings.RestartServiceRules)
        {
            var row = anomalies.FirstOrDefault(row => policy.TriggerProcesses.Any(name => SafetyPolicy.Normalize(name) == SafetyPolicy.Normalize(row.Name)));
            if (Check("service:" + policy.Name, row is not null, row?.CommitGb, 0, monotonicSeconds) && row is not null)
                output.Add(new("service_leak", "服务宿主进程疑似泄漏", $"{row.Name}触发已配置服务{policy.Name}的观察规则。", "warning",
                    row.Name, row.Pid, row.CreatedTicks, "restart_service", policy.Name));
        }
        foreach (var policy in settings.TrimProcesses)
        {
            var row = rows.Where(row => SafetyPolicy.Normalize(row.Name) == SafetyPolicy.Normalize(policy.Name)).OrderByDescending(row => row.MemoryGb).FirstOrDefault();
            var trim = settings.MemoryTrimEnabled && snapshot.MemoryPercent >= settings.TrimPressurePercent && row?.MemoryGb >= policy.MinWorkingSetGb;
            if (Check("trim:" + policy.Name, trim, row?.MemoryGb, 0, monotonicSeconds) && row is not null)
                output.Add(new("working_set_large", "白名单后台工作集偏大", "可回收工作集物理页；这不等于减少提交内存或修复泄漏。", "info",
                    row.Name, row.Pid, row.CreatedTicks, "mem_trim", policy.Name));
        }
        foreach (var key in lastSeen.Where(pair => monotonicSeconds - pair.Value > 1800).Select(pair => pair.Key).ToArray())
        { lastSeen.Remove(key); histories.Remove(key); redlineNotified.Remove(key); windows.Remove("leak:" + key); }
        return output;
    }

    bool Check(string key, bool? condition, double? value, double duration, double now, bool hysteresis = false)
    {
        if (!windows.TryGetValue(key, out var state)) windows[key] = state = new Window();
        if (!value.HasValue || !condition.HasValue) { state.Since = null; return false; }
        if (hysteresis && state.Latched && value <= settings.Alerts.CommitExit) { state.Latched = false; state.Since = null; return false; }
        var active = condition.Value || (hysteresis && state.Latched);
        if (!active) { state.Since = null; return false; }
        state.Since ??= now;
        if (now - state.Since.Value < duration || now < state.Next) return false;
        state.Next = now + settings.Alerts.CooldownSeconds;
        state.Since = null;
        if (hysteresis) state.Latched = true;
        return true;
    }

    static double? Metric(SystemSnapshot snapshot, params string[] names)
    {
        foreach (var name in names)
            if (snapshot.Metrics?.TryGetValue(name, out var metric) == true && metric.State == "ok") return metric.Value;
        return null;
    }
}

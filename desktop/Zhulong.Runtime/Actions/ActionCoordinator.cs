using System.ComponentModel;
using System.Diagnostics;
using Zhulong.Core;

namespace Zhulong.Runtime.Actions;

public sealed partial class ActionCoordinator : IAsyncDisposable
{
    readonly RuntimeSettings settings;
    readonly IProcessBoundary processes;
    readonly IServiceBoundary services;
    readonly ActionStateFile state;
    readonly Func<ActionResult, Task>? audit;
    readonly Func<double> monotonic;
    readonly Func<DateTimeOffset> utcNow;
    readonly Func<TimeSpan, CancellationToken, Task> delay;
    readonly SemaphoreSlim gate = new(1, 1);
    readonly CancellationTokenSource stopping = new();
    readonly List<(ActionStamp Stamp, double At)> attempts = [];

    public ActionCoordinator(RuntimeSettings settings, IProcessBoundary? processes = null,
        IServiceBoundary? services = null, string? statePath = null, Func<ActionResult, Task>? audit = null,
        Func<double>? monotonic = null, Func<DateTimeOffset>? utcNow = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        this.settings = settings;
        this.processes = processes ?? new NativeProcessBoundary();
        this.services = services ?? new NativeServiceBoundary();
        this.audit = audit;
        this.monotonic = monotonic ?? (() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        this.delay = delay ?? ((duration, token) => Task.Delay(duration, token));
        state = new ActionStateFile(statePath);
        var now = this.utcNow();
        var mono = this.monotonic();
        foreach (var stamp in state.State.Attempts)
        {
            var age = Math.Max(0, (now - stamp.At).TotalSeconds);
            if (age <= 3600) attempts.Add((stamp, mono - age));
        }
    }

    public async Task<ActionResult> ExecuteAsync(string action, AlertDecision decision,
        CancellationToken cancellationToken, bool automatic = false)
    {
        var target = decision.Target ?? decision.ProcessName ?? "";
        if (automatic && !settings.AutomaticActions)
            return new("skipped:automatic_disabled", action, target);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopping.Token);
        var entered = false;
        ActionResult result;
        try
        {
            await gate.WaitAsync(linked.Token).ConfigureAwait(false);
            entered = true;
            result = await Task.Run(async () => action switch
            {
                "kill_process" => Kill(target, decision),
                "mem_trim" => Trim(target, decision),
                "restart_service" => await RestartServiceAsync(target, linked.Token).ConfigureAwait(false),
                _ => new ActionResult("skipped:unknown_action", action, target)
            }, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { result = new("cancelled", action, target); }
        catch (Win32Exception error) { result = new("degraded:access_denied", action, target, "Win32 " + error.NativeErrorCode); }
        catch (InvalidOperationException error) { result = new("skipped:" + error.Message, action, target); }
        catch (Exception error) { result = new("degraded:exception", action, target, error.Message); }
        // 未取得gate的取消请求没有执行动作，退出后不能再触碰审计数据库。
        if (!entered) return result;
        try { return await AuditAsync(result).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    ActionResult Kill(string target, AlertDecision decision)
    {
        const string action = "kill_process";
        if (SafetyPolicy.NameBlocked(target)) return new("blocked:protected_process", action, target);
        var policy = settings.WatchProcesses.FirstOrDefault(rule => Equal(rule.Name, target));
        policy ??= settings.AutoKill.Any(name => Equal(name, target)) ? new ProcessPolicy(target, 20) : null;
        if (policy is null) return new("skipped:not_whitelisted", action, target);
        if (!state.Readable) return new("degraded:accounting_unavailable", action, target);
        if (OverLimit(action, target, policy.MaxActionsPerTenMinutes, 600)) return new("degraded:frequency_cap", action, target);
        // 不采用规则快照的旧水位；重新读取同名实例，再完整走安全链。
        var process = processes.Find(target).OrderByDescending(value => value.CommitGb).FirstOrDefault();
        if (process is null) return new("skipped:process_gone", action, target);
        if (process.CommitGb < policy.MaxCommitGb) return new("skipped:commit_below_threshold", action, target);
        if (SafetyPolicy.ProcessBlocked(process, processes.CurrentPid) is { } protectedReason)
            return new("blocked:" + protectedReason, action, target);
        if (!OldEnough(process, policy.MinAgeSeconds)) return new("skipped:too_young", action, target);
        if (!PathMatches(process, policy.PathContains)) return new("skipped:path_mismatch", action, target);
        if (!MatchesDecision(process, decision)) return new("skipped:pid_reused", action, target);
        var fresh = processes.Read(process.Pid);
        if (fresh is null) return new("skipped:process_gone", action, target);
        if (fresh.CreatedTicks != process.CreatedTicks) return new("skipped:pid_reused", action, target);
        processes.Terminate(fresh);
        return Account(action, target, "killed", "PID " + fresh.Pid);
    }

    ActionResult Trim(string target, AlertDecision decision)
    {
        const string action = "mem_trim";
        if (SafetyPolicy.NameBlocked(target)) return new("blocked:protected_process", action, target);
        var policy = settings.TrimProcesses.FirstOrDefault(rule => Equal(rule.Name, target));
        if (policy is null) return new("skipped:not_whitelisted", action, target);
        if (!state.Readable) return new("degraded:accounting_unavailable", action, target);
        if (OverLimit(action, target, policy.MaxTrimsPerTenMinutes, 600)) return new("degraded:frequency_cap", action, target);
        var process = processes.Find(target).OrderByDescending(value => value.WorkingSetGb).FirstOrDefault();
        if (process is null) return new("skipped:process_gone", action, target);
        if (process.WorkingSetGb < Math.Max(2,policy.MinWorkingSetGb)) return new("skipped:ws_below_threshold", action, target);
        if (SafetyPolicy.ProcessBlocked(process, processes.CurrentPid) is { } reason) return new("blocked:" + reason, action, target);
        if (!OldEnough(process, Math.Max(300, policy.MinAgeSeconds))) return new("skipped:too_young", action, target);
        if (!PathMatches(process, policy.PathContains)) return new("skipped:path_mismatch", action, target);
        if (!MatchesDecision(process, decision)) return new("skipped:pid_reused", action, target);
        var fresh = processes.Read(process.Pid);
        if (fresh is null) return new("skipped:process_gone", action, target);
        if (fresh.CreatedTicks != process.CreatedTicks) return new("skipped:pid_reused", action, target);
        processes.Trim(fresh);
        return Account(action, target, "trimmed", "仅回收工作集物理页，提交内存不变；PID " + fresh.Pid);
    }

    bool OldEnough(ProcessIdentity process, int seconds) => process.CreatedTicks > 0 &&
        (utcNow().UtcTicks - process.CreatedTicks) / (double)TimeSpan.TicksPerSecond >= seconds;
    static bool PathMatches(ProcessIdentity process, string? contains) => string.IsNullOrEmpty(contains) ||
        (!string.IsNullOrEmpty(process.ExecutablePath) && process.ExecutablePath.Contains(contains, StringComparison.OrdinalIgnoreCase));
    static bool MatchesDecision(ProcessIdentity process, AlertDecision decision) =>
        (!decision.Pid.HasValue || decision.Pid.Value == process.Pid) &&
        (!decision.CreatedTicks.HasValue || decision.CreatedTicks.Value == process.CreatedTicks);
    static bool Equal(string left, string right) => SafetyPolicy.Normalize(left) == SafetyPolicy.Normalize(right);

    bool OverLimit(string action, string target, int maximum, double window)
    {
        var now = monotonic();
        attempts.RemoveAll(value => now - value.At > 3600);
        return attempts.Count(value => value.Stamp.Action == action && Equal(value.Stamp.Target, target) && now - value.At < window) >= maximum;
    }
    void Record(string action, string target)
    {
        attempts.Add((new ActionStamp(action, target, utcNow()), monotonic()));
        state.State.Attempts = attempts.Select(value => value.Stamp).ToList();
        state.Save();
    }
    ActionResult Account(string action, string target, string status, string? detail = null)
    {
        try { Record(action, target); return new(status, action, target, detail); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return new("degraded:accounting_failed", action, target, status + "已发生；内存限额保留；" + error.Message); }
    }
    async Task<ActionResult> AuditAsync(ActionResult result)
    {
        if (audit is null) return result;
        try { await audit(result).ConfigureAwait(false); return result; }
        catch (Exception error) { return new("degraded:audit_failed", result.Action, result.Target, result.Status + "；" + error.Message); }
    }
    public async Task StopAsync()
    {
        stopping.Cancel();
        await RestoreThrottleAsync(CancellationToken.None).ConfigureAwait(false);
    }
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        gate.Dispose();
        stopping.Dispose();
    }
}

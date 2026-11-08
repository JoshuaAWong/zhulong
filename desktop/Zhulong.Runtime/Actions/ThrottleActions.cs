using System.ComponentModel;
using Zhulong.Core;

namespace Zhulong.Runtime.Actions;

public sealed partial class ActionCoordinator
{
    public async Task<IReadOnlyList<ActionResult>> ApplyThrottleAsync(CancellationToken token = default)
    {
        if (!settings.ThrottleEnabled) return [];
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, stopping.Token);
        await gate.WaitAsync(linked.Token).ConfigureAwait(false);
        var results = new List<ActionResult>();
        try
        {
            try
            {
            await Task.Run(() =>
            {
                var count = processes.ProcessorCount;
                if (count is < 1 or > 64)
                { results.Add(new("skipped:processor_groups_unsupported", "throttle", "")); return; }
                var cursor = count;
                foreach (var rule in settings.ThrottleRules)
                {
                    linked.Token.ThrowIfCancellationRequested();
                    if (SafetyPolicy.NameBlocked(rule.Name))
                    { results.Add(new("blocked:protected_process", "throttle", rule.Name)); continue; }
                    if (rule.Cores < 1 || rule.Priority is not ("normal" or "below_normal") || rule.Mode is not ("spread" or "tail" or "explicit"))
                    { results.Add(new("skipped:invalid_rule", "throttle", rule.Name)); continue; }
                    ulong wanted=0;
                    if(rule.Mode=="explicit")
                    {
                        if(rule.CpuIndices is null||rule.CpuIndices.Count==0||rule.CpuIndices.Any(index=>index<0||index>=count))
                        {results.Add(new("skipped:invalid_rule","throttle",rule.Name));continue;}
                        foreach(var index in rule.CpuIndices)wanted|=1UL<<index;
                    }
                    else
                    {
                        var size=Math.Min(rule.Cores,count);
                        var upper=rule.Mode=="tail"?count:cursor;
                        var lower=rule.Mode=="tail"?count-size:Math.Max(count/2,cursor-size);
                        if(lower==upper){lower=count/2;upper=Math.Min(count,lower+size);}
                        if(rule.Mode=="spread")cursor=lower;
                        for(var index=lower;index<upper;index++)wanted|=1UL<<index;
                    }
                    foreach (var process in processes.Find(rule.Name))
                    {
                        if (SafetyPolicy.ProcessBlocked(process, processes.CurrentPid) is { } reason)
                        { results.Add(new("blocked:" + reason, "throttle", rule.Name)); continue; }
                        try
                        {
                            var current = processes.ReadLimits(process);
                            var affinity = current.SystemAffinity & wanted;
                            if (affinity == 0) { results.Add(new("skipped:no_allowed_cores", "throttle", rule.Name)); continue; }
                            var priority = rule.Priority == "normal" ? 0x20U : 0x4000U;
                            if (current.Priority == priority && current.Affinity == affinity) continue;
                            var applied = state.State.Applied.FirstOrDefault(value => value.Process.Pid == process.Pid && value.Process.CreatedTicks == process.CreatedTicks);
                            if (applied is null)
                            {
                                var pending = new AppliedLimit(process, current);
                                state.State.Applied.Add(pending);
                                // 不能保存恢复信息时，绝不先改变目标属性。
                                try { state.Save(); }
                                catch { state.State.Applied.Remove(pending); throw; }
                            }
                            processes.SetLimits(process, priority, affinity);
                            results.Add(new("throttled", "throttle", rule.Name, "PID " + process.Pid));
                        }
                        catch (Exception error) when (error is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
                        { results.Add(new("degraded:throttle_failed", "throttle", rule.Name, error.Message)); }
                    }
                }
            }, linked.Token).ConfigureAwait(false);
            }
            finally
            {
                // 即使后续目标取消/失败，已发生操作的审计也在同一在途屏障内结束。
                for (var i = 0; i < results.Count; i++) results[i] = await AuditAsync(results[i]).ConfigureAwait(false);
            }
            return results;
        }
        finally { gate.Release(); }
    }

    public async Task<IReadOnlyList<ActionResult>> RestoreThrottleAsync(CancellationToken token = default)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        var results = new List<ActionResult>();
        try
        {
            try
            {
            await Task.Run(() =>
            {
                if (!state.Readable)
                { results.Add(new("degraded:restore_state_unavailable", "restore_throttle", "")); return; }
                foreach (var applied in state.State.Applied.ToArray())
                {
                    token.ThrowIfCancellationRequested();
                    var target = applied.Process.Name;
                    try
                    {
                        var fresh = processes.Read(applied.Process.Pid);
                        if (fresh is null || fresh.CreatedTicks != applied.Process.CreatedTicks)
                        {
                            state.State.Applied.Remove(applied);
                            results.Add(new(fresh is null ? "skipped:process_gone" : "skipped:pid_reused", "restore_throttle", target));
                            continue;
                        }
                        var reason = SafetyPolicy.ProcessBlocked(fresh, processes.CurrentPid);
                        if (reason is not null && reason != "foreground")
                        { results.Add(new("blocked:" + reason, "restore_throttle", target)); continue; }
                        processes.SetLimits(fresh, applied.Original.Priority, applied.Original.Affinity);
                        state.State.Applied.Remove(applied);
                        results.Add(new("restored", "restore_throttle", target, "PID " + fresh.Pid));
                    }
                    catch (Exception error) when (error is Win32Exception or InvalidOperationException)
                    { results.Add(new("degraded:restore_failed", "restore_throttle", target, error.Message)); }
                }
                try { state.Save(); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { results.Add(new("degraded:accounting_failed", "restore_throttle", "", "已执行的恢复不回滚；" + error.Message)); }
            }, token).ConfigureAwait(false);
            }
            finally
            {
                for (var i = 0; i < results.Count; i++) results[i] = await AuditAsync(results[i]).ConfigureAwait(false);
            }
            return results;
        }
        finally { gate.Release(); }
    }
}

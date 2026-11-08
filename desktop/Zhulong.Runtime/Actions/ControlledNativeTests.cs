using System.Diagnostics;
using System.Text.Json;
using Zhulong.Core;

namespace Zhulong.Runtime.Actions;

public static partial class RuntimeSafetyTests
{
    public static async Task<int> RunNativeAsync(string helperExecutable, string outputDirectory)
    {
        var helper = Path.GetFullPath(helperExecutable);
        if (!File.Exists(helper) || Path.GetFileName(helper) != "Zhulong.NativeFixture.exe")
            throw new ArgumentException("只接受本轮构建的Zhulong.NativeFixture.exe。");
        var directory = Path.Combine(Path.GetFullPath(outputDirectory), "native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var ready = Path.Combine(directory, "ready.txt");
        var nonce = Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(helper)! };
        start.ArgumentList.Add(ready); start.ArgumentList.Add(nonce);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("受控helper启动失败。");
        _ = child.SafeHandle;
        var assertions = 0;
        void Check(bool value, string message) { assertions++; if (!value) throw new InvalidOperationException(message); }
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (!File.Exists(ready))
            {
                Check(!child.HasExited, "受控helper在就绪前退出。");
                await Task.Delay(50, deadline.Token).ConfigureAwait(false);
            }
            Check(File.ReadAllText(ready, System.Text.Encoding.UTF8) == child.Id + ":" + nonce, "受控helper身份握手不符。");
            var native = new NativeProcessBoundary();
            var identity = native.Read(child.Id) ?? throw new InvalidOperationException("受控helper已消失。");
            Check(identity.Pid == child.Id && identity.Pid != Environment.ProcessId &&
                string.Equals(identity.ExecutablePath, helper, StringComparison.OrdinalIgnoreCase), "目标必须是本轮启动的完整路径/PID。");
            Check(identity.ProtectionReason is null && !SafetyPolicy.NameBlocked(identity.Name), "受控helper不得命中任何保护信号。");
            var original = native.ReadLimits(identity);
            var settings = new RuntimeSettings
            {
                AutomaticActions = false, ThrottleEnabled = true,
                WatchProcesses = [new ProcessPolicy(identity.Name, .001, 0, 1, helper)],
                ThrottleRules = [new ThrottlePolicy(identity.Name, 1)], TrimProcesses = [], AutoKill = [],
                RestartServices = [], RestartServiceRules = []
            };
            await using var coordinator = new ActionCoordinator(settings, native, new NativeServiceBoundary(),
                Path.Combine(directory, "action-state.json"));
            var throttled = await coordinator.ApplyThrottleAsync(deadline.Token).ConfigureAwait(false);
            Check(throttled.Any(value => value.Status == "throttled"), "受控helper真实限流未成功。");
            var changed = native.ReadLimits(identity);
            Check(changed.Priority == 0x4000 && changed.Affinity != 0, "真实优先级/亲和性未达到目标。");
            var restored = await coordinator.RestoreThrottleAsync(deadline.Token).ConfigureAwait(false);
            Check(restored.Any(value => value.Status == "restored") && native.ReadLimits(identity) == original, "真实限流原值恢复失败。");
            // 这里只证明EmptyWorkingSet系统边界成功；生产300秒eligibility不在此测试中绕过。
            var beforeTrim = native.Read(child.Id)!;
            native.Trim(beforeTrim);
            var afterTrim = native.Read(child.Id)!;
            Check(afterTrim.CreatedTicks == identity.CreatedTicks, "整理期间受控helper身份发生变化。");
            var decision = new AlertDecision("controlled_native", "受控原生验收", "仅处置本轮自建helper", "info",
                identity.Name, identity.Pid, identity.CreatedTicks, Target: identity.Name);
            var killed = await coordinator.ExecuteAsync("kill_process", decision, deadline.Token).ConfigureAwait(false);
            Check(killed.Status == "killed", "受控helper真实安全kill未成功：" + killed.Status + " " + killed.Detail);
            await child.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            Check(child.HasExited, "受控helper未确认退出。");
            var evidence = new { status = "PASS", assertions, helper, pid = child.Id, original, changed,
                trim_before_gb = beforeTrim.WorkingSetGb, trim_after_gb = afterTrim.WorkingSetGb,
                trim_scope = "native_boundary_only; production_minimum_age_remains_300_seconds", killed,
                real_services_touched = false, unrelated_user_processes_touched = false };
            File.WriteAllText(Path.Combine(directory, "native-actions.json"), JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }), System.Text.Encoding.UTF8);
            return assertions;
        }
        finally
        {
            // 固定Process句柄只清理自己创建的helper，绝不按名字批量终止。
            if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync().ConfigureAwait(false); }
        }
    }
}

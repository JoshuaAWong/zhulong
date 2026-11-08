using System.ComponentModel;
using Zhulong.Core;

namespace Zhulong.Runtime.Actions;

public sealed partial class ActionCoordinator
{
    async Task<ActionResult> RestartServiceAsync(string target, CancellationToken token)
    {
        const string action = "restart_service";
        if (SafetyPolicy.ServiceBlocked(target)) return new("blocked:never_restart", action, target);
        var policy = settings.RestartServiceRules.FirstOrDefault(value => value.Name.Equals(target, StringComparison.OrdinalIgnoreCase));
        if (policy is null && !settings.RestartServices.Contains(target, StringComparer.OrdinalIgnoreCase))
            return new("skipped:not_whitelisted", action, target);
        if (!state.Readable) return new("degraded:accounting_unavailable", action, target);
        if (OverLimit(action, target, policy?.MaxRestartsPerHour ?? 1, 3600)) return new("degraded:frequency_cap", action, target);
        // 发起即占额度；记录失败时不开始服务操作，避免反复敲打失败服务。
        try { Record(action, target); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return new("degraded:accounting_failed", action, target, error.Message); }
        if (services.Status(target) != ServiceState.Running) return new("skipped:not_running", action, target);
        token.ThrowIfCancellationRequested();
        services.Stop(target);
        try
        {
            if (!await WaitServiceAsync(target, ServiceState.Stopped, 30, token).ConfigureAwait(false))
                return new("degraded:stop_timeout", action, target, "停止未确认，不终结服务宿主进程。");
            if (await StartServiceAsync(target, token).ConfigureAwait(false)) return new("restarted", action, target);
            return new("degraded:start_failed", action, target, "启动失败，需要人工检查服务依赖。");
        }
        catch (OperationCanceledException)
        {
            // 已发出stop后取消不能直接留下已停止服务；恢复仅针对同一个显式服务。
            if (services.Status(target) == ServiceState.Running) return new("cancelled:running", action, target);
            if (await WaitServiceAsync(target, ServiceState.Stopped, 30, CancellationToken.None).ConfigureAwait(false) &&
                await StartServiceAsync(target, CancellationToken.None).ConfigureAwait(false))
                return new("cancelled:restored_running", action, target);
            return new("degraded:cancel_recovery_failed", action, target, "服务启动恢复未确认，需要人工处理。");
        }
    }

    async Task<bool> WaitServiceAsync(string target, ServiceState wanted, int seconds, CancellationToken token)
    {
        var deadline = monotonic() + seconds;
        while (monotonic() < deadline)
        {
            token.ThrowIfCancellationRequested();
            if (services.Status(target) == wanted) return true;
            await delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
        }
        return services.Status(target) == wanted;
    }

    async Task<bool> StartServiceAsync(string target, CancellationToken token)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try { services.Start(target); }
            catch (Win32Exception error) when (error.NativeErrorCode != 5) { }
            if (await WaitServiceAsync(target, ServiceState.Running, 30, token).ConfigureAwait(false)) return true;
            if (attempt < 2) await delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
        }
        return false;
    }
}

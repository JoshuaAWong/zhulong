using Zhulong.Core;
using Zhulong.Runtime.Rules;

namespace Zhulong.Runtime.Actions;

// 全部动作使用替身；不会终结、整理、限流或重启任何真实目标。
public static partial class RuntimeSafetyTests
{
    public static async Task<int> RunAsync()
    {
        var assertions = 0;
        void Check(bool value, string message) { assertions++; if (!value) throw new InvalidOperationException(message); }
        var now = DateTimeOffset.UtcNow;
        var policy = new ProcessPolicy("fixture.exe", 15, 60, 2, "fixture-folder");
        RuntimeSettings Settings() => new() { WatchProcesses = [policy], TrimProcesses = [new("fixture.exe",PathContains:"fixture-folder")], AutoKill = [], ThrottleRules = [new("fixture.exe")], RestartServices = ["FixtureService"] };
        FakeProcesses Fake() => new(new ProcessIdentity(7654321, "fixture.exe", now.AddMinutes(-10).UtcTicks, 16, 3, "C:/fixture-folder/fixture.exe"));
        AlertDecision Decision(FakeProcesses p) => new("fixture", "测试", "测试", "warning", p.Value.Name, p.Value.Pid, p.Value.CreatedTicks);
        async Task<ActionResult> Kill(FakeProcesses p, RuntimeSettings? settings = null, Func<ActionResult, Task>? audit = null)
        {
            await using var coordinator = new ActionCoordinator(settings ?? Settings(), p, new FakeServices(), audit: audit, utcNow: () => now);
            return await coordinator.ExecuteAsync("kill_process", Decision(p), CancellationToken.None);
        }
        var fresh = Fake(); var result = await Kill(fresh);
        Check(result.Status == "killed" && fresh.Kills == 1, "合法白名单链应执行一次替身kill：" + result.Status + " " + result.Detail);
        var below = Fake(); below.Value = below.Value with { CommitGb = 14 };
        Check((await Kill(below)).Status == "skipped:commit_below_threshold" && below.Kills == 0, "重读水位回落必须拒绝");
        var young = Fake(); young.Value = young.Value with { CreatedTicks = now.AddSeconds(-30).UtcTicks };
        Check((await Kill(young)).Status == "skipped:too_young", "不足龄必须拒绝");
        var wrongPath = Fake(); wrongPath.Value = wrongPath.Value with { ExecutablePath = "C:/other/fixture.exe" };
        Check((await Kill(wrongPath)).Status == "skipped:path_mismatch", "路径不匹配必须拒绝");
        var noPath = Fake(); noPath.Value = noPath.Value with { ExecutablePath = null };
        Check((await Kill(noPath)).Status == "skipped:path_mismatch", "配置路径时读取不到路径必须fail-closed");
        var reused = Fake(); reused.ReuseOnRead = true;
        Check((await Kill(reused)).Status == "skipped:pid_reused" && reused.Kills == 0, "创建时间变化必须拒绝");
        var self = Fake(); self.SelfPid = self.Value.Pid;
        Check((await Kill(self)).Status == "blocked:self" && self.Kills == 0, "永不处理自身");
        var security = Fake(); security.Value = security.Value with { Name = "MsMpEng.exe" };
        var securitySettings = Settings(); securitySettings.WatchProcesses = [new("MsMpEng.exe", 0)];
        Check((await Kill(security, securitySettings)).Status == "blocked:protected_process" && security.FindCalls == 0, "安全软件先于枚举硬阻断");
        var notAllowed = Fake(); var empty = Settings(); empty.WatchProcesses = [];
        Check((await Kill(notAllowed, empty)).Status == "skipped:not_whitelisted", "非白名单拒绝");
        var automated = Fake();
        await using (var coordinator = new ActionCoordinator(Settings(), automated, new FakeServices(), utcNow: () => now))
            Check((await coordinator.ExecuteAsync("kill_process", Decision(automated), CancellationToken.None, automatic: true)).Status == "skipped:automatic_disabled" && automated.FindCalls == 0, "自动动作默认关闭");
        var capped = Fake(); double mono = 100;
        await using (var coordinator = new ActionCoordinator(Settings(), capped, new FakeServices(), monotonic: () => mono, utcNow: () => now))
        {
            await coordinator.ExecuteAsync("kill_process", Decision(capped), CancellationToken.None);
            await coordinator.ExecuteAsync("kill_process", Decision(capped), CancellationToken.None);
            Check((await coordinator.ExecuteAsync("kill_process", Decision(capped), CancellationToken.None)).Status == "degraded:frequency_cap" && capped.Kills == 2, "每目标十分钟封顶");
            mono += 601;
            Check((await coordinator.ExecuteAsync("kill_process", Decision(capped), CancellationToken.None)).Status == "killed", "单调窗口到期后恢复额度");
        }
        var audited = Fake();
        Check((await Kill(audited, audit: _ => throw new IOException("模拟事件库失败"))).Status == "degraded:audit_failed" && audited.Kills == 1, "动作后审计失败必须降级且不重复动作");
        var trimmed = Fake();
        await using (var coordinator = new ActionCoordinator(Settings(), trimmed, new FakeServices(), utcNow: () => now))
            Check((await coordinator.ExecuteAsync("mem_trim", Decision(trimmed), CancellationToken.None)).Status == "trimmed" && trimmed.Trims == 1, "工作集整理使用同一拒绝链");

        var throttled = Fake(); var throttleSettings = Settings(); throttleSettings.ThrottleEnabled = true;
        var original = throttled.Limits;
        await using (var coordinator = new ActionCoordinator(throttleSettings, throttled, new FakeServices(), utcNow: () => now))
        {
            var applied = await coordinator.ApplyThrottleAsync();
            Check(applied.Any(value => value.Status == "throttled") && throttled.Limits.Priority == 0x4000, "限流应保存并变更原属性");
            await coordinator.RestoreThrottleAsync();
            Check(throttled.Limits == original, "恢复原优先级和原亲和性，而非猜normal/all");
            await coordinator.ApplyThrottleAsync();
            throttled.Value = throttled.Value with { CreatedTicks = throttled.Value.CreatedTicks + 1 };
            var sets = throttled.Sets;
            Check((await coordinator.RestoreThrottleAsync()).Any(value => value.Status == "skipped:pid_reused") && throttled.Sets == sets, "恢复时PID复用不得变更新进程");
        }

        foreach(var mode in new[]{"tail","explicit"})
        {
            var target=Fake();var config=Settings();config.ThrottleEnabled=true;
            config.ThrottleRules=[new("fixture.exe",1,Mode:mode,CpuIndices:[0,2])];
            await using var coordinator=new ActionCoordinator(config,target,new FakeServices(),utcNow:()=>now);
            await coordinator.ApplyThrottleAsync();
            Check(target.Limits.Affinity==(mode=="tail"?0b1000UL:0b0101UL),"尾核与显式核号必须按配置施加："+mode);
            await coordinator.RestoreThrottleAsync();Check(target.Limits.Affinity==0b0011UL,"新限流口径仍恢复原值："+mode);
        }
        var services = new FakeServices(); var serviceSettings = Settings();
        var serviceDecision = new AlertDecision("fixture", "测试", "测试", "warning", Target: "FixtureService");
        mono = 0;
        await using (var coordinator = new ActionCoordinator(serviceSettings, Fake(), services, monotonic: () => mono,
            utcNow: () => now, delay: (duration, _) => { mono += duration.TotalSeconds; return Task.CompletedTask; }))
        {
            Check((await coordinator.ExecuteAsync("restart_service", serviceDecision, CancellationToken.None)).Status == "restarted" && services.Starts == 1, "服务stop/start完整链");
            Check((await coordinator.ExecuteAsync("restart_service", serviceDecision, CancellationToken.None)).Status == "degraded:frequency_cap", "服务发起即限额");
        }
        services = new FakeServices { NeverStops = true }; mono = 0;
        await using (var coordinator = new ActionCoordinator(serviceSettings, Fake(), services, monotonic: () => mono,
            utcNow: () => now, delay: (duration, _) => { mono += duration.TotalSeconds; return Task.CompletedTask; }))
            Check((await coordinator.ExecuteAsync("restart_service", serviceDecision, CancellationToken.None)).Status == "degraded:stop_timeout" && services.Starts == 0, "停止超时不杀宿主或强启");
        using var cancellation = new CancellationTokenSource();
        services = new FakeServices { AfterStop = cancellation.Cancel }; mono = 0;
        await using (var coordinator = new ActionCoordinator(serviceSettings, Fake(), services, monotonic: () => mono,
            utcNow: () => now, delay: (duration, _) => { mono += duration.TotalSeconds; return Task.CompletedTask; }))
            Check((await coordinator.ExecuteAsync("restart_service", serviceDecision, cancellation.Token)).Status == "cancelled:restored_running" && services.Current == ServiceState.Running, "服务停止后取消必须补偿恢复");

        var stateDirectory = Path.Combine(Path.GetTempPath(), "zhulong-safety-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stateDirectory);
        var invalid = Path.Combine(stateDirectory, "invalid.json");
        File.WriteAllText(invalid, "{", System.Text.Encoding.UTF8);
        var corruptTarget = Fake();
        await using (var coordinator = new ActionCoordinator(Settings(), corruptTarget, new FakeServices(), invalid, utcNow: () => now))
            Check((await coordinator.ExecuteAsync("kill_process", Decision(corruptTarget), CancellationToken.None)).Status == "degraded:accounting_unavailable" && corruptTarget.Kills == 0,
                "损坏历史不得重置额度后继续动作");
        Check(File.ReadAllText(invalid, System.Text.Encoding.UTF8) == "{", "损坏历史保留原件");
        var failingPath = Path.Combine(stateDirectory, "directory-not-file");
        Directory.CreateDirectory(failingPath);
        var accounting = Fake(); var oneAction = Settings(); oneAction.WatchProcesses = [policy with { MaxActionsPerTenMinutes = 1 }];
        await using (var coordinator = new ActionCoordinator(oneAction, accounting, new FakeServices(), failingPath, utcNow: () => now))
        {
            Check((await coordinator.ExecuteAsync("kill_process", Decision(accounting), CancellationToken.None)).Status == "degraded:accounting_failed" && accounting.Kills == 1,
                "动作后持久化失败明确降级");
            Check((await coordinator.ExecuteAsync("kill_process", Decision(accounting), CancellationToken.None)).Status == "degraded:frequency_cap" && accounting.Kills == 1,
                "记账失败仍由内存封顶，不能重复处置");
        }
        var cannotPersist = Fake();
        await using (var coordinator = new ActionCoordinator(throttleSettings, cannotPersist, new FakeServices(), failingPath, utcNow: () => now))
        {
            await coordinator.ApplyThrottleAsync(); await coordinator.ApplyThrottleAsync();
            Check(cannotPersist.Sets == 0, "不能持久化恢复信息时两次尝试都不改变属性");
        }
        var persistent = Fake(); var statePath = Path.Combine(stateDirectory, "persisted.json");
        await using (var firstCoordinator = new ActionCoordinator(throttleSettings, persistent, new FakeServices(), statePath, utcNow: () => now))
        {
            var initial = persistent.Limits;
            await firstCoordinator.ApplyThrottleAsync();
            await using (var recovered = new ActionCoordinator(throttleSettings, persistent, new FakeServices(), statePath, utcNow: () => now))
            {
                Check((await recovered.RestoreThrottleAsync()).Any(value => value.Status == "restored") && persistent.Limits == initial,
                    "重新读取持久化记录后恢复原属性");
            }
        }
        services = new FakeServices { NeverStarts = true }; mono = 0;
        await using (var coordinator = new ActionCoordinator(serviceSettings, Fake(), services, monotonic: () => mono,
            utcNow: () => now, delay: (duration, _) => { mono += duration.TotalSeconds; return Task.CompletedTask; }))
            Check((await coordinator.ExecuteAsync("restart_service", serviceDecision, CancellationToken.None)).Status == "degraded:start_failed" && services.Starts == 3,
                "服务启动失败经过限定三次尝试后降级");

        var auditEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAudit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var auditTarget = Fake();
        await using (var coordinator = new ActionCoordinator(Settings(), auditTarget, new FakeServices(),
            audit: async value => { if (value.Action == "kill_process") { auditEntered.TrySetResult(); await releaseAudit.Task.ConfigureAwait(false); } }, utcNow: () => now))
        {
            var execution = coordinator.ExecuteAsync("kill_process", Decision(auditTarget), CancellationToken.None);
            await auditEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var stop = coordinator.StopAsync();
            await Task.Delay(50);
            try { Check(!stop.IsCompleted && auditTarget.Kills == 1, "实际动作完成但审计未结束时Stop不能提前释放"); }
            finally { releaseAudit.TrySetResult(); }
            Check((await execution).Status == "killed", "解除审计阻塞后返回真实结果");
            await stop.WaitAsync(TimeSpan.FromSeconds(5));
            var auditsBefore = auditTarget.Kills;
            Check((await coordinator.ExecuteAsync("kill_process", Decision(auditTarget), CancellationToken.None)).Status == "cancelled" && auditTarget.Kills == auditsBefore,
                "停止后未入gate请求不再执行或审计");
        }
        auditEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        releaseAudit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inFlightThrottle = Fake();
        var originalLimits = inFlightThrottle.Limits;
        var throttleCoordinator = new ActionCoordinator(throttleSettings, inFlightThrottle, new FakeServices(),
            audit: async value => { if (value.Action == "throttle") { auditEntered.TrySetResult(); await releaseAudit.Task.ConfigureAwait(false); } }, utcNow: () => now);
        var applying = throttleCoordinator.ApplyThrottleAsync();
        await auditEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disposing = throttleCoordinator.DisposeAsync().AsTask();
        await Task.Delay(50);
        try { Check(!disposing.IsCompleted, "限流审计未结束时Dispose不能越过在途屏障"); }
        finally { releaseAudit.TrySetResult(); }
        await applying;
        await disposing.WaitAsync(TimeSpan.FromSeconds(5));
        Check(inFlightThrottle.Limits == originalLimits, "在途审计结束后退出恢复原属性");

        SystemSnapshot Snapshot(double? commit, double? totalCpu = 20, double? core = 98, double processCommit = 10) =>
            new(now, commit, 20, 30, 60, 20, 32, totalCpu, [new(456, "growing.exe", 3, 1, processCommit, CreatedTicks: now.AddHours(-1).UtcTicks)],
                Metrics: new() { ["cpu_max_core"] = new("cpu_max_core", core, "%") });
        var engine = new RuleEngine(new RuntimeSettings { WatchProcesses = [] });
        Check(engine.Evaluate(Snapshot(86), 0).Count == 0 && engine.Evaluate(Snapshot(86), 89).Count == 0, "持续窗口未到90秒不触发");
        var at90 = engine.Evaluate(Snapshot(86), 90);
        Check(at90.Any(value => value.Rule == "commit_high") && at90.Any(value => value.Rule == "single_core_hot"), "提交和单核规则真实取到指标");
        Check(!engine.Evaluate(Snapshot(82), 120).Any(value => value.Rule == "commit_high"), "迟滞冷却中不重复");
        Check(engine.Evaluate(Snapshot(82), 690).Any(value => value.Rule == "commit_high"), "已触发后80到85保持高位迟滞");
        engine.Evaluate(Snapshot(80), 700);
        Check(!engine.Evaluate(Snapshot(86), 1300).Any(value => value.Rule == "commit_high") &&
            engine.Evaluate(Snapshot(86), 1390).Any(value => value.Rule == "commit_high"), "回落80后重新计满持续窗口");
        var growth = new RuleEngine(new RuntimeSettings { WatchProcesses = [] });
        growth.Evaluate(Snapshot(40, core: 10, processCommit: 5), 0);
        Check(growth.Evaluate(Snapshot(40, core: 10, processCommit: 10), 30).Any(value => value.Rule == "anomaly_leak"), "按创建身份识别增长，不能只按大小");
        var configured=new RuleEngine(new RuntimeSettings{WatchProcesses=[],Alerts=new(70,60,30,60,90,80,30)});
        Check(configured.Evaluate(Snapshot(75),0).Count==0&&configured.Evaluate(Snapshot(75),30).Any(x=>x.Rule=="commit_high"),"配置水位和持续时长必须真实生效");
        return assertions;
    }

    sealed class FakeProcesses(ProcessIdentity identity) : IProcessBoundary
    {
        public ProcessIdentity Value = identity;
        public int SelfPid = 99, Kills, Trims, Sets, FindCalls;
        public bool ReuseOnRead;
        public ProcessLimits Limits = new(0x80, 0b0011, 0b1111);
        public int CurrentPid => SelfPid;
        public int ProcessorCount => 4;
        public IReadOnlyList<ProcessIdentity> Find(string name) { FindCalls++; return [Value]; }
        public ProcessIdentity? Read(int pid) => ReuseOnRead ? Value with { CreatedTicks = Value.CreatedTicks + 1 } : Value;
        public void Terminate(ProcessIdentity expected) => Kills++;
        public void Trim(ProcessIdentity expected) => Trims++;
        public ProcessLimits ReadLimits(ProcessIdentity expected) => Limits;
        public void SetLimits(ProcessIdentity expected, uint priority, ulong affinity) { Sets++; Limits = Limits with { Priority = priority, Affinity = affinity }; }
    }
    sealed class FakeServices : IServiceBoundary
    {
        public ServiceState Current = ServiceState.Running;
        public bool NeverStops, NeverStarts;
        public int Starts;
        public Action? AfterStop;
        public ServiceState Status(string service) => Current;
        public void Stop(string service) { Current = NeverStops ? ServiceState.StopPending : ServiceState.Stopped; AfterStop?.Invoke(); }
        public void Start(string service) { Starts++; Current = NeverStarts ? ServiceState.Stopped : ServiceState.Running; }
    }
}

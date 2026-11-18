using System.Buffers.Binary;
using System.ComponentModel;
using Zhulong.Core;
using Zhulong.Runtime.Application;
using Zhulong.Runtime.Collectors;
using Zhulong.Runtime.Rules;
using Zhulong.Runtime.Storage;

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
        {
            var outcome = await coordinator.ExecuteAsync("mem_trim", Decision(trimmed), CancellationToken.None);
            Check(outcome.Status == "trimmed" && outcome.PrivateWorkingSetReductionBytes == 1048576 && trimmed.Trims == 1,
                "工作集整理返回实测的专用工作集差值");
            Check((await coordinator.ExecuteAsync("mem_trim", Decision(trimmed), CancellationToken.None)).Status == "degraded:frequency_cap" &&
                trimmed.Trims == 1, "重复点击不能绕过工作集整理限频");
        }
        var manualOnly = Settings();
        manualOnly.AutomaticActions = true;
        manualOnly.TrimProcesses = [new("fixture.exe", PathContains: "fixture-folder", ManualMinWorkingSetMiB: 32)];
        var smallTarget = Fake();
        smallTarget.Value = smallTarget.Value with { WorkingSetGb = .10 };
        var manualTrim = Decision(smallTarget) with { Rule = "manual_trim" };
        await using (var coordinator = new ActionCoordinator(manualOnly, smallTarget, new FakeServices(), utcNow: () => now))
        {
            Check((await coordinator.ExecuteAsync("mem_trim", Decision(smallTarget), CancellationToken.None)).Status == "skipped:ws_below_threshold" &&
                (await coordinator.ExecuteAsync("mem_trim", manualTrim, CancellationToken.None, automatic: true)).Status == "skipped:ws_below_threshold",
                "确认规则建议和自动执行不得使用较低的手动工作集门槛");
            smallTarget.ReuseOnRead = true;
            Check((await coordinator.ExecuteAsync("mem_trim", manualTrim, CancellationToken.None)).Status == "skipped:pid_reused" && smallTarget.Trims == 0,
                "较低手动门槛不得跳过创建时间复核");
            smallTarget.ReuseOnRead = false;
            Check((await coordinator.ExecuteAsync("mem_trim", manualTrim, CancellationToken.None)).Status == "trimmed" && smallTarget.Trims == 1,
                "手动白名单可使用已配置的32MiB门槛");
            Check((await coordinator.ExecuteAsync("mem_trim", manualTrim, CancellationToken.None)).Status == "degraded:frequency_cap" && smallTarget.Trims == 1,
                "较低手动门槛继续使用同一频控");
        }
        SettingsStore.Validate(manualOnly);
        foreach (var invalidTrim in new[] { new TrimPolicy("fixture.exe", PathContains: " ", ManualMinWorkingSetMiB: 32),
                     new TrimPolicy("fixture.exe", PathContains: "fixture-folder", ManualMinWorkingSetMiB: 31),
                     new TrimPolicy("fixture.exe", PathContains: "fixture-folder", ManualMinWorkingSetMiB: 2049) })
        {
            manualOnly.TrimProcesses = [invalidTrim];
            var rejected = false;
            try { SettingsStore.Validate(manualOnly); }
            catch (ArgumentException) { rejected = true; }
            Check(rejected, "较低的手动门槛必须受路径与32–2048MiB取值范围约束");
        }

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
            Check((await coordinator.ExecuteAsync("restart_service", serviceDecision, CancellationToken.None)).Status == "restarted" &&
                services.Opens == 1 && services.Stops == 1 && services.Starts == 1 && services.Closes == 1, "服务停止、启动复用同一会话并关闭");
            Check((await coordinator.ExecuteAsync("restart_service", serviceDecision, CancellationToken.None)).Status == "degraded:frequency_cap", "服务发起即限额");
        }
        foreach (var (code, status) in new[] { (5, "degraded:access_denied"), (1060, "skipped:service_not_found") })
        {
            services = new FakeServices { OpenError = code };
            await using var coordinator = new ActionCoordinator(serviceSettings, Fake(), services, utcNow: () => now);
            Check((await coordinator.ExecuteAsync("restart_service", serviceDecision, CancellationToken.None)).Status == status &&
                services.Opens == 1 && services.Stops == 0 && services.Starts == 0, "无法取得完整重启权限或服务不存在时不得启停");
        }
        services = new FakeServices { NeverStops = true }; mono = 0;
        await using (var coordinator = new ActionCoordinator(serviceSettings, Fake(), services, monotonic: () => mono,
            utcNow: () => now, delay: (duration, _) => { mono += duration.TotalSeconds; return Task.CompletedTask; }))
            Check((await coordinator.ExecuteAsync("restart_service", serviceDecision, CancellationToken.None)).Status == "degraded:stop_timeout" && services.Starts == 0, "停止超时不杀宿主或强启");
        using var cancellation = new CancellationTokenSource();
        services = new FakeServices { NeverStops = true, AfterStop = cancellation.Cancel }; mono = 0;
        await using (var coordinator = new ActionCoordinator(serviceSettings, Fake(), services, monotonic: () => mono,
            utcNow: () => now, delay: (duration, _) => { mono += duration.TotalSeconds; services.Current = ServiceState.Stopped; return Task.CompletedTask; }))
            Check((await coordinator.ExecuteAsync("restart_service", serviceDecision, cancellation.Token)).Status == "cancelled:restored_running" &&
                services.Current == ServiceState.Running && services.Opens == 1 && services.Closes == 1 && services.Starts == 1, "正在停止时取消必须等待并复用会话恢复");
        using var startCancellation = new CancellationTokenSource();
        services = new FakeServices(); mono = 0;
        services.AfterStart = () => { services.Current = ServiceState.StartPending; startCancellation.Cancel(); };
        await using (var coordinator = new ActionCoordinator(serviceSettings, Fake(), services, monotonic: () => mono,
            utcNow: () => now, delay: (duration, _) => { mono += duration.TotalSeconds; services.Current = ServiceState.Running; return Task.CompletedTask; }))
            Check((await coordinator.ExecuteAsync("restart_service", serviceDecision, startCancellation.Token)).Status == "cancelled:running" &&
                services.Current == ServiceState.Running && services.Opens == 1 && services.Closes == 1 && services.Starts == 1, "正在启动时取消须确认自然恢复，避免重复启动");

        var stateDirectory = Path.Combine(Path.GetTempPath(), "zhulong-safety-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stateDirectory);
        var trimDirectory = Path.Combine(stateDirectory, "manual-trim");
        var absent = "zhulong-fixture-" + Guid.NewGuid().ToString("N");
        var manualTrimSettings = new RuntimeSettings
        {
            AutomaticActions = false, MemoryTrimEnabled = false, TrimPressurePercent = 100,
            TrimProcesses = [new(absent + ".exe"), new(absent)], RestartServices = ["FixtureService"]
        };
        new SettingsStore(trimDirectory).Save(manualTrimSettings);
        using (var runtime = new RuntimeHost(trimDirectory))
        {
            var trimResults = await runtime.TrimWorkingSetsAsync();
            Check(trimResults is [{ Status: "skipped:process_gone", Action: "mem_trim" }],
                "点击不受自动整理开关和压力阈值限制，同名目标仅检查一次且仅提交工作集整理");
        }
        manualTrimSettings.TrimProcesses = [];
        new SettingsStore(trimDirectory).Save(manualTrimSettings);
        using (var runtime = new RuntimeHost(trimDirectory))
            Check((await runtime.TrimWorkingSetsAsync()).Count == 0, "没有工作集白名单时不得提交服务或其他处置");
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
        var at90 = engine.Evaluate(Snapshot(86) with
        {
            CommitGrowth = new("ready", 10, [new(456, "growing.exe", now.UtcTicks, 10, 1.25, 10, 8)])
        }, 90);
        Check(at90.Any(value => value.Rule == "commit_high") && at90.Any(value => value.Rule == "single_core_hot"), "提交和单核规则真实取到指标");
        var commitAlert = at90.Single(value => value.Rule == "commit_high");
        Check(commitAlert.Text.Contains("growing.exe") && commitAlert.Text.Contains("10 分钟") &&
            commitAlert.Text.Contains("距上限") && commitAlert.SuggestedAction is null,
            "提交告警展示采样证据和余量，不生成处置动作");
        Check(!engine.Evaluate(Snapshot(82), 120).Any(value => value.Rule == "commit_high"), "迟滞冷却中不重复");
        Check(engine.Evaluate(Snapshot(82), 690).Any(value => value.Rule == "commit_high"), "已触发后80到85保持高位迟滞");
        engine.Evaluate(Snapshot(80), 700);
        Check(!engine.Evaluate(Snapshot(86), 1300).Any(value => value.Rule == "commit_high") &&
            engine.Evaluate(Snapshot(86), 1390).Any(value => value.Rule == "commit_high"), "回落80后重新计满持续窗口");
        var growth = new RuleEngine(new RuntimeSettings { WatchProcesses = [] });
        growth.Evaluate(Snapshot(40, core: 10, processCommit: 5), 0);
        Check(growth.Evaluate(Snapshot(40, core: 10, processCommit: 10), 30).Any(value => value.Rule == "anomaly_leak"), "按创建身份识别增长，不能只按大小");
        var configured=new RuleEngine(new RuntimeSettings{WatchProcesses=[],Alerts=new(70,60,30,60,90,80,30)});
        Check(configured.Evaluate(Snapshot(75), 0).Count == 0, "配置水位仍遵循持续时长");
        var configuredAlert = configured.Evaluate(Snapshot(75), 30).Single(x => x.Rule == "commit_high");
        Check(configuredAlert.Text.Contains("70%") && configuredAlert.Text.Contains("60%") &&
            configuredAlert.Text.Contains("进程榜") && !configuredAlert.Text.Contains("growing.exe"),
            "自定义阈值真实生效，无增长数据时提供排查入口");
        CheckCommitGrowth(Check, now.UtcTicks);
        CheckCpuTemperature(Check, now);
        return assertions;
    }

    static void CheckCpuTemperature(Action<bool, string> check, DateTimeOffset now)
    {
        // 按 Core Temp 公布的字节偏移构造数据，独立验证解析器的结构布局。
        var buffer = new byte[2686];
        check(CoreTempSensor.Decode(buffer) is null && CoreTempSensor.Decode(buffer.AsSpan(0, 2685)) is null,
            "尚未初始化或长度不足的 CPU 温度数据应视为未知");
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(1536), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(1540), 2);
        void Temperature(int index, float value) =>
            BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(1544 + index * sizeof(float)), value);

        Temperature(0, 45.5f);
        Temperature(1, float.NaN);
        Temperature(2, -1);
        Temperature(3, 67.25f);
        Temperature(4, 99);
        check(CoreTempSensor.Decode(buffer) == 67.25, "CPU 温度取已声明核心中的最高有效摄氏温度");

        buffer[2684] = 1;
        Temperature(0, 95);
        Temperature(3, 140);
        check(CoreTempSensor.Decode(buffer) == 60, "CPU 华氏绝对温度应转为摄氏温度");

        buffer[2685] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(1024), 203);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(1028), 194);
        Temperature(0, 18);
        Temperature(1, 36);
        Temperature(2, 0);
        Temperature(3, 18);
        check(CoreTempSensor.Decode(buffer) == 90, "华氏 TjMax 要转为绝对摄氏温度，零温差有效，每颗 CPU 使用自己的 TjMax");

        using var slowMetrics = new SlowMetrics(new());
        check(slowMetrics.Snapshot(now)[CoreTempSensor.MetricName] is { Value: null, Unit: "°C", State: "not_sampled" },
            "慢采集没有提供 CPU 温度前应返回未知");
    }

    static void CheckCommitGrowth(Action<bool, string> check, long created)
    {
        ProcessRow Row(double commit, long? identity = null) => new(12, "fixture.exe", 1, null, commit, CreatedTicks: identity ?? created);
        var steady = new CommitGrowthTracker(30);
        var slow = new CommitGrowthTracker(30);
        var spike = new CommitGrowthTracker(30);
        CommitGrowthReport? longReading = null;
        for (var seconds = 0; seconds <= 3600; seconds += 30)
        {
            var observed = steady.Observe([Row(2 + 7.6 * seconds / 60 / 1024)], seconds);
            var slowReading = slow.Observe([Row(2 + 5d * seconds / 60 / 1024)], seconds);
            var spiked = spike.Observe([Row(seconds < 480 ? 2 : 3 + (seconds - 480) / 1024d / 60)], seconds);
            if (seconds == 0)
                check(observed.State == "warming" && observed.Rows.Count == 0, "缺少足够观察时长时先显示预热");
            if (seconds == 600)
                check(observed.Rows is [{ Minutes: >= 10, GrowthGb: > .07 }] && slowReading.Rows.Count == 0,
                    "约每分钟增加7.6MiB的趋势应进入短窗口，较慢变化继续积累");
            if (seconds == 3600)
            {
                longReading = observed;
                check(slowReading.Rows is [{ Minutes: >= 60, GrowthGb: > .28, IncreasingIntervals: >= 3 }],
                    "较慢趋势可凭60分钟分段证据出现");
                check(spiked.State == "ready" && spiked.Rows.Count == 0, "单次大幅跳变和少量漂移不作持续增长");
            }
        }
        check(longReading?.Rows is [{ Minutes: >= 60, GrowthGb: > .43 }], "同一进程只返回一条更充分的增长证据");
        var reused = steady.Observe([Row(8, created + 1)], 3630);
        check(reused.State == "warming" && reused.Rows.Count == 0, "PID复用不得继承前一实例的提交内存基线");
        check(steady.Observe([new(12, "fixture.exe", 1, null, 8)], 3660).State == "unavailable" &&
            steady.Observe([Row(9, created + 1)], 3690).Rows.Count == 0, "不可读实例身份打断连续窗口");
        check(slow.Observe([Row(9)], 3900).State == "warming", "长采样间断后不得拼接旧基线");
        var sparse = new CommitGrowthTracker(300);
        for (var i = 0; i < 3; i++) sparse.Observe([Row(2 + i * .04)], i * 300);
        var sparseResult = sparse.Observe([Row(2.12)], 900);
        check(sparseResult.WindowMinutes == 15 && sparseResult.Rows is [{ Minutes: 15, IncreasingIntervals: 3 }],
            "300秒采样仍需至少三个可比较的上升间隔");
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
        public long? Trim(ProcessIdentity expected) { Trims++; return 1048576; }
        public ProcessLimits ReadLimits(ProcessIdentity expected) => Limits;
        public void SetLimits(ProcessIdentity expected, uint priority, ulong affinity) { Sets++; Limits = Limits with { Priority = priority, Affinity = affinity }; }
    }
    sealed class FakeServices : IServiceBoundary, IServiceSession
    {
        public ServiceState Current = ServiceState.Running;
        public bool NeverStops, NeverStarts;
        public int OpenError, Opens, Closes, Stops, Starts;
        public Action? AfterStop, AfterStart;
        public IServiceSession OpenForRestart(string service)
        {
            Opens++;
            if (OpenError != 0) throw new Win32Exception(OpenError);
            return this;
        }
        public ServiceState Status() => Current;
        public void Stop() { Stops++; Current = NeverStops ? ServiceState.StopPending : ServiceState.Stopped; AfterStop?.Invoke(); }
        public void Start() { Starts++; Current = NeverStarts ? ServiceState.Stopped : ServiceState.Running; AfterStart?.Invoke(); }
        public void Dispose() => Closes++;
    }
}

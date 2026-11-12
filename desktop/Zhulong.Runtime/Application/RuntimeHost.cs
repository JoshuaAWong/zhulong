using System.Diagnostics;
using System.Text.Json;
using Zhulong.Core;
using Zhulong.Runtime.Actions;
using Zhulong.Runtime.Collectors;
using Zhulong.Runtime.Providers.AI;
using Zhulong.Runtime.Rules;
using Zhulong.Runtime.Storage;
namespace Zhulong.Runtime.Application;

// 唯一后台属主：独立节拍，无重叠；UI只读快照，慢IO不阻塞快采样。
public sealed class RuntimeHost : IDisposable
{
    readonly object storeGate=new(), lifecycleGate=new(), decisionGate=new();
    readonly string directory;
    readonly MonitorStore store;
    readonly SystemMonitor monitor;
    readonly RuleEngine rules;
    readonly ActionCoordinator actions;
    readonly AiUsageService usage;
    readonly CancellationTokenSource stopped=new();
    readonly SemaphoreSlim throttleWorkflow=new(1,1);
    readonly Dictionary<string,AlertDecision> pending=new(),manualActions=new();
    readonly Dictionary<string,AlertDecision> automaticPending=new();
    readonly Dictionary<string,string> failures=new();
    Task[] loops=[];Task? stopTask,activeManual;Task<object>? activeImport;
    int started;double pausedUntil,mutedUntil,nextThrottle;SystemSnapshot? latest;
    NativeCounters.CoreTimes[] readoutBaseline = [];
    LiveSystemReadout? liveReadout;
    public RuntimeSettings Settings {get;}
    public LiveSystemReadout? LiveReadout => Volatile.Read(ref liveReadout);
    public event Action<SystemSnapshot>? Sampled;
    public event Action<RuntimeEvent>? Notified;
    public event Action<LiveSystemReadout>? ReadoutChanged;
    public RuntimeHost(string directory)
    {
        this.directory=directory;Directory.CreateDirectory(directory);
        Settings=new SettingsStore(directory).Load();
        store=new(Path.Combine(directory,"monitor.db"));monitor=new(Settings);rules=new(Settings);
        actions=new(Settings,statePath:Path.Combine(directory,"actions.json"),audit:result=>
        { Record(new(DateTimeOffset.UtcNow,"处置 · "+result.Action,result.Target+" · "+result.Status+" · "+result.Detail,result.Status=="ok"?"info":"warning","action"));return Task.CompletedTask; });
        usage=new(Path.Combine(directory,"ai.db"));
        foreach(var policy in Settings.TrimProcesses)
            manualActions[Guid.NewGuid().ToString("N")]=new("manual_trim","手动整理白名单工作集 · "+policy.Name,$"将复核工作集{policy.MinWorkingSetGb:F1}GB、存活{policy.MinAgeSeconds}秒、路径及频次；不结束进程，不降低提交内存。","info",ProcessName:policy.Name,SuggestedAction:"mem_trim",Target:policy.Name);
        foreach(var service in Settings.RestartServices.Concat(Settings.RestartServiceRules.Select(p=>p.Name)).Distinct(StringComparer.OrdinalIgnoreCase))
            manualActions[Guid.NewGuid().ToString("N")]=new("manual_service","重启白名单服务 · "+service,"连接此服务的设备或应用可能暂时断开；将先确认运行状态、权限与频次。","warning",SuggestedAction:"restart_service",Target:service);
    }
    static double Clock=>Stopwatch.GetTimestamp()/(double)Stopwatch.Frequency;
    public bool Paused=>Clock<Volatile.Read(ref pausedUntil);
    public bool Muted=>Clock<Volatile.Read(ref mutedUntil);
    public void PauseFiveMinutes(){var until=Clock+300;Volatile.Write(ref pausedUntil,until);Volatile.Write(ref mutedUntil,until);lock(decisionGate)automaticPending.Clear();}
    public void MuteFiveMinutes()=>Volatile.Write(ref mutedUntil,Clock+300);
    public void Resume(){Volatile.Write(ref pausedUntil,0);Volatile.Write(ref mutedUntil,0);}
    public void Start()
    {
        if(Interlocked.Exchange(ref started,1)!=0)return;
        loops=[RunLoop("采样",TimeSpan.FromSeconds(Settings.SampleSeconds),SampleAsync),
               RunLoop("实时读数",TimeSpan.FromSeconds(3),ReadoutAsync),
               RunLoop("慢采集",TimeSpan.FromSeconds(60),()=>monitor.CollectSlowAsync(stopped.Token)),
               RunLoop("安全动作",TimeSpan.FromSeconds(5),ProcessActionsAsync),
               RunLoop("AI统计",TimeSpan.FromSeconds(60),async()=>{await usage.RefreshAsync(stopped.Token);}),
               RunLoop("自身资源",TimeSpan.FromHours(1),()=>{using var process=Process.GetCurrentProcess();lock(storeGate)store.SaveSelf(DateTimeOffset.UtcNow,process.PrivateMemorySize64,process.WorkingSet64,process.HandleCount,process.TotalProcessorTime.TotalSeconds);return Task.CompletedTask;}),
               RunLoop("历史清理",TimeSpan.FromHours(6),()=>{lock(storeGate)store.Cleanup(Settings.RetentionDays);return Task.CompletedTask;})];
    }
    Task RunLoop(string name,TimeSpan period,Func<Task> run)=>Task.Run(async()=>
    {
        using var timer=new PeriodicTimer(period);
        do
        {
            try {await run().ConfigureAwait(false);lock(failures)failures.Remove(name);}
            catch(OperationCanceledException)when(stopped.IsCancellationRequested){break;}
            catch(Exception error)when(error is not (OutOfMemoryException or StackOverflowException))
            {
                bool changed;lock(failures){changed=!failures.TryGetValue(name,out var old)||old!=error.Message;failures[name]=error.Message;}
                if(changed)WriteDiagnostic(name,error.Message);
            }
        }while(await timer.WaitForNextTickAsync(stopped.Token).ConfigureAwait(false));
    },stopped.Token);
    Task ReadoutAsync()
    {
        // 显示单独计算CPU差分，完整采样保留自身周期和基线。
        var current = NativeCounters.CpuTimes();
        var (cpu, _, _) = NativeCounters.CpuLoad(readoutBaseline, current);
        readoutBaseline = current;
        var (physical, used, total) = NativeCounters.PhysicalMemory();
        var value = new LiveSystemReadout(DateTimeOffset.UtcNow, cpu, physical, used, total);
        Volatile.Write(ref liveReadout, value);
        ReadoutChanged?.Invoke(value);
        return Task.CompletedTask;
    }

    Task SampleAsync()
    {
        var value=monitor.Collect();
        var decisions=rules.Evaluate(value,Clock).ToArray();
        var events=decisions.Select(d=>new RuntimeEvent(value.Timestamp,d.Title,d.Text,d.Level,"alert",d.ProcessName)).ToList();
        var prior=Volatile.Read(ref latest);
        if(!string.IsNullOrEmpty(value.Error)&&value.Error!=prior?.Error)events.Add(new(value.Timestamp,"部分指标采集失败",value.Error,"warning"));
        Volatile.Write(ref latest,value);
        Exception? storageError=null;
        try{lock(storeGate)store.Save(value,events);}
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException){storageError=error;}
        lock(decisionGate)
        {
            foreach(var d in decisions.Where(x=>x.SuggestedAction is not null))pending[Guid.NewGuid().ToString("N")]=d;
            while(pending.Count>30)pending.Remove(pending.Keys.First());
        }
        foreach(var item in events)if(!Muted&&Settings.Notifications)Notified?.Invoke(item);
        Sampled?.Invoke(value);
        try{WriteHeartbeat(value.Timestamp);lock(failures)failures.Remove("心跳");}
        catch(Exception error)when(error is IOException or UnauthorizedAccessException){lock(failures)failures["心跳"]=error.Message;WriteDiagnostic("心跳",error.Message);}
        // 存储失败仍发布实时采样和提醒；当轮自动处置不入队。
        if(storageError is not null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(storageError).Throw();
        if(!Paused&&Settings.AutomaticActions)lock(decisionGate)
        {
            foreach(var d in decisions.Where(x=>x.SuggestedAction is not null))automaticPending[d.Rule+":"+d.Target]=d;
            while(automaticPending.Count>30)automaticPending.Remove(automaticPending.Keys.First());
        }
        return Task.CompletedTask;
    }
    async Task ProcessActionsAsync()
    {
        if(Paused)return;
        AlertDecision[] batch;
        lock(decisionGate){batch=automaticPending.Values.Take(8).ToArray();foreach(var d in batch)automaticPending.Remove(d.Rule+":"+d.Target);}
        foreach(var d in batch){if(Paused)break;await actions.ExecuteAsync(d.SuggestedAction!,d,stopped.Token,automatic:true).ConfigureAwait(false);}
        if(!Paused&&Settings.ThrottleEnabled&&Clock>=Volatile.Read(ref nextThrottle))
        {
            await throttleWorkflow.WaitAsync(stopped.Token).ConfigureAwait(false);
            try{if(!Paused&&Settings.ThrottleEnabled&&Clock>=Volatile.Read(ref nextThrottle)){Volatile.Write(ref nextThrottle,Clock+120);await actions.ApplyThrottleAsync(stopped.Token).ConfigureAwait(false);}}
            finally{throttleWorkflow.Release();}
        }
    }
    public async Task<object?> QueryAsync(string api,string? metric=null,int hours=24)
    {
        switch(api)
        {
            case "snapshot":return Volatile.Read(ref latest);
            case "history":
                if(metric is null||hours is not(24 or 168 or 720))throw new ArgumentException("不支持的历史查询。");
                return await Task.Run(()=>new {points=store.History(metric,hours)},stopped.Token);
            case "events":return await Task.Run(()=>new {events=store.Events(hours)},stopped.Token);
            case "usage":return Json(await usage.ReportAsync(stopped.Token));
            case "refresh-usage":return Json(await usage.RefreshAsync(stopped.Token));
            case "refresh-codex-account":return Json(await usage.RefreshAccountAsync(stopped.Token));
            case "health":lock(failures)return new {paused=Paused,muted=Muted,failures=new Dictionary<string,string>(failures),sampleSeconds=Settings.SampleSeconds};
            case "actions":lock(decisionGate)return pending.Concat(manualActions).Select(x=>new {id=x.Key,title=x.Value.Title,text=x.Value.Text,action=x.Value.SuggestedAction,target=x.Value.Target??x.Value.ProcessName}).ToArray();
            default:throw new ArgumentException("不支持的面板请求。");
        }
    }
    static JsonElement Json(string text){using var json=JsonDocument.Parse(text);return json.RootElement.Clone();}
    public AlertDecision? FindAction(string id){lock(decisionGate)return pending.GetValueOrDefault(id)??manualActions.GetValueOrDefault(id);}
    public Task<ActionResult> ExecuteConfirmedAsync(string id)=>RunManual(()=>ExecuteConfirmedCoreAsync(id));
    async Task<ActionResult> ExecuteConfirmedCoreAsync(string id)
    {
        AlertDecision? decision;lock(decisionGate){if(!pending.Remove(id,out decision))decision=manualActions.GetValueOrDefault(id);}
        if(decision?.SuggestedAction is null)return new("rejected","unknown","","动作已失效，请重新查看告警。");
        return await actions.ExecuteAsync(decision.SuggestedAction,decision,stopped.Token).ConfigureAwait(false);
    }
    public Task<IReadOnlyList<ActionResult>> ToggleThrottleAsync()=>RunManual(ToggleThrottleCoreAsync);
    async Task<IReadOnlyList<ActionResult>> ToggleThrottleCoreAsync()
    {
        await throttleWorkflow.WaitAsync(stopped.Token).ConfigureAwait(false);
        try
        {
            var target=!Settings.ThrottleEnabled;
            var saved=new SettingsStore(directory).Load();saved.ThrottleEnabled=target;new SettingsStore(directory).Save(saved);
            Settings.ThrottleEnabled=target;Volatile.Write(ref nextThrottle,target?Clock+120:0);
            return target?await actions.ApplyThrottleAsync(stopped.Token).ConfigureAwait(false):await actions.RestoreThrottleAsync(stopped.Token).ConfigureAwait(false);
        }
        finally{throttleWorkflow.Release();}
    }
    public Task<IReadOnlyList<ActionResult>> ResetThrottleAsync()=>RunManual(ResetThrottleCoreAsync);
    async Task<IReadOnlyList<ActionResult>> ResetThrottleCoreAsync()
    {
        await throttleWorkflow.WaitAsync(stopped.Token).ConfigureAwait(false);
        try
        {
            var defaults=new RuntimeSettings().ThrottleRules;
            var saved=new SettingsStore(directory).Load();saved.ThrottleRules=defaults;new SettingsStore(directory).Save(saved);
            var result=(await actions.RestoreThrottleAsync(stopped.Token).ConfigureAwait(false)).ToList();Settings.ThrottleRules=defaults;
            Volatile.Write(ref nextThrottle,Clock+120);
            if(Settings.ThrottleEnabled&&!result.Any(x=>x.Status.StartsWith("degraded:")||x.Status.StartsWith("blocked:")))
                result.AddRange(await actions.ApplyThrottleAsync(stopped.Token).ConfigureAwait(false));
            return result;
        }
        finally{throttleWorkflow.Release();}
    }
    Task<T> RunManual<T>(Func<Task<T>> operation)
    {
        lock(lifecycleGate)
        {
            if(stopped.IsCancellationRequested)throw new InvalidOperationException("新版正在退出。");
            if(activeManual is{IsCompleted:false})throw new InvalidOperationException("已有操作在进行，请等待结果。");
            var task=Task.Run(operation);activeManual=task;return task;
        }
    }
    public Task<object> ImportLegacyAsync(string source)
    {lock(lifecycleGate){if(stopped.IsCancellationRequested)throw new InvalidOperationException("新版正在退出。");if(activeImport is{IsCompleted:false})throw new InvalidOperationException("已有历史导入在进行。");return activeImport=Task.Run(()=>{using var archive=new MonitorStore(Path.Combine(directory,"legacy.db"));return archive.ImportLegacy(source,stopped.Token);},stopped.Token);}}
    public void Record(RuntimeEvent value){lock(storeGate)store.AddEvent(value);}
    public SystemSnapshot CollectDiagnostic()=>monitor.Collect();
    public object Diagnostics()
    {
        using var process=Process.GetCurrentProcess();
        lock(failures)return new {timestamp=DateTimeOffset.UtcNow,version="0.2.0-replacement",sample=Volatile.Read(ref latest),paused=Paused,muted=Muted,failures=new Dictionary<string,string>(failures),self=new {pid=Environment.ProcessId,privateBytes=process.PrivateMemorySize64,workingSet=process.WorkingSet64,handles=process.HandleCount,totalCpuSeconds=process.TotalProcessorTime.TotalSeconds}};
    }
    void WriteHeartbeat(DateTimeOffset timestamp)
    {using var process=Process.GetCurrentProcess();var path=Path.Combine(directory,"heartbeat.json");File.WriteAllText(path+".tmp",JsonSerializer.Serialize(new {pid=Environment.ProcessId,createdTicks=process.StartTime.ToUniversalTime().Ticks,timestamp,managedExecutable=Environment.ProcessPath,sampleSeconds=Settings.SampleSeconds}),System.Text.Encoding.UTF8);File.Move(path+".tmp",path,true);}
    void WriteDiagnostic(string job,string message)
    {
        lock(lifecycleGate)
        {
            var path=Path.Combine(directory,"runtime-errors.jsonl");
            try{if(File.Exists(path)&&new FileInfo(path).Length>1024*1024)File.Move(path,path+".previous",true);File.AppendAllText(path,JsonSerializer.Serialize(new {timestamp=DateTimeOffset.UtcNow,job,message})+"\n",System.Text.Encoding.UTF8);}
            catch(IOException){}catch(UnauthorizedAccessException){}
        }
    }
    public Task StopAsync(){lock(lifecycleGate)return stopTask??=StopCoreAsync();}
    async Task StopCoreAsync()
    {
        stopped.Cancel();
        foreach(var loop in loops)try{await loop.ConfigureAwait(false);}catch(OperationCanceledException){}
        if(activeManual is not null)try{await activeManual.ConfigureAwait(false);}catch(OperationCanceledException){}catch(Exception error)when(error is not(OutOfMemoryException or StackOverflowException)){WriteDiagnostic("手动操作",error.Message);}
        if(activeImport is not null)try{await activeImport.ConfigureAwait(false);}catch(OperationCanceledException){}catch(Exception error)when(error is not(OutOfMemoryException or StackOverflowException)){WriteDiagnostic("历史导入",error.Message);}
        await actions.DisposeAsync().ConfigureAwait(false);
        await usage.DisposeAsync().ConfigureAwait(false);
    }
    public void Dispose(){StopAsync().GetAwaiter().GetResult();monitor.Dispose();store.Dispose();throttleWorkflow.Dispose();stopped.Dispose();}
}

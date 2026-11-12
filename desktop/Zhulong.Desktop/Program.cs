using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using Forms = System.Windows.Forms;

namespace Zhulong.Desktop;

internal static class Program
{
    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    static Application? application;
    static DataHub? hub;
    static DesktopWindow? window;
    static Forms.NotifyIcon? tray;
    static System.Drawing.Icon? trayIcon;
    static string directory = "";
    static bool quitting;
    static bool restartRequested;
    static LocalPortal? portal;
    static SettingsWindow? settingsWindow;
    static AiAccountSettingsWindow? aiSettingsWindow;
    static readonly object logGate = new();
    static readonly SemaphoreSlim portalOperation=new(1,1);
    const string InstanceMutexName = "Local\\ZhulongNextInstance";

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--self-test") return DesktopSelfTests.Run(args[1]);
        if(args.Length==3&&args[0]=="--self-test-native")return NativeSafety(args[1],args[2]);
        if(args.Length==3&&args[0]=="--runtime-probe")return RuntimeProbe.Run(args[1],int.Parse(args[2]));
        if(args.Length==1&&args[0]=="--uninstall-tasks"){StartupTasks.RemoveOwnedAsync(Path.Combine(AppContext.BaseDirectory,"data")).GetAwaiter().GetResult();return 0;}
        if(args.Length==2&&args[0]=="--diagnose")return WriteDiagnostics(args[1]);
        if (args.Length > 0 && (args[0] == "--enable-logon-startup" || args[0] == "--disable-logon-startup"))
            return args.Length == 1 ? ConfigureLogonFromCommandLine(args[0] == "--enable-logon-startup") : 2;
        var dataIndex=Array.IndexOf(args,"--data-dir");
        if(dataIndex>=0&&(dataIndex+1>=args.Length||args[dataIndex+1].StartsWith("--",StringComparison.Ordinal)))return 2;
        directory = dataIndex>=0 ? Path.GetFullPath(args[dataIndex+1]) : Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(directory);
        var background = args.Contains("--background") || args.Contains("--watchdog");
        var suppress = Path.Combine(directory,"intentional-exit");
        if(args.Contains("--watchdog") && File.Exists(suppress))return 0;

        // 新版使用独立名字，不接管或改动旧烛龙。
        using var wake = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\ZhulongNextOpen");
        using var singleton = new Mutex(true, InstanceMutexName, out var first);
        var owns=first;
        if(owns&&args.Contains("--watchdog")){var reserved=WatchdogRecovery.ReserveStartup(directory,Environment.ProcessPath!);WriteLifecycle("watchdog_startup",reserved);if(!reserved.Recovered){singleton.ReleaseMutex();return reserved.Status.StartsWith("degraded:")||reserved.Status.StartsWith("blocked:")?1:0;}}
        if(!owns&&args.Contains("--watchdog"))
        {
            var recovery=WatchdogRecovery.Recover(directory,Environment.ProcessPath!);WriteLifecycle("watchdog",recovery);
            if(recovery.Recovered)try{owns=singleton.WaitOne(TimeSpan.FromSeconds(5));}catch(AbandonedMutexException){owns=true;}
        }
        if(!owns){if(!background)wake.Set();return 0;}
        if(!args.Contains("--watchdog"))File.Delete(suppress);
        application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        RegisteredWaitHandle? registration = null;
        application.Startup += (_, _) =>
        {
            try
            {
                hub = new DataHub(directory);
                var menu = new Forms.ContextMenuStrip();
                menu.Items.Add("打开烛龙新版（朱印）", null, (_, _) => OpenWindow());
                menu.Items.Add("在浏览器打开新版",null,async(_,_)=>await NativeAsync(OpenBrowserAsync));
                menu.Items.Add("关闭浏览器入口",null,async(_,_)=>await NativeAsync(ClosePortalAsync));
                menu.Items.Add("暂停自动处置与提醒5分钟", null, (_, _) => hub.PauseFiveMinutes());
                menu.Items.Add("静音提醒5分钟", null, (_, _) => hub.MuteFiveMinutes());
                menu.Items.Add("恢复自动处置与提醒", null, (_, _) => hub.Resume());
                menu.Items.Add("切换限流", null, async (_, _) => await NativeAsync(async()=> { var result=await hub.ToggleThrottleAsync(); ShowActionResults(result,"限流结果"); }));
                menu.Items.Add("恢复默认限流规则", null, async (_, _) => await NativeAsync(async()=>ShowActionResults(await hub.ResetThrottleAsync(),"恢复默认规则结果")));
                menu.Items.Add("新版设置", null, async (_, _) => await NativeAsync(()=>{OpenSettings();return Task.CompletedTask;}));
                menu.Items.Add("AI 账户", null, (_, _) => OpenAiSettings());
                menu.Items.Add("导入旧版历史…", null, async (_, _) => await NativeAsync(ImportAsync));
                menu.Items.Add("导出诊断…", null, async (_, _) => await NativeAsync(()=>{ExportDiagnostics();return Task.CompletedTask;}));
                menu.Items.Add("登录后后台自启…", null, async (_, _) => await NativeAsync(() => ConfigureStartupAsync(StartupTasks.Kind.Logon)));
                menu.Items.Add("5 分钟存活守护…", null, async (_, _) => await NativeAsync(() => ConfigureStartupAsync(StartupTasks.Kind.Watchdog)));
                menu.Items.Add(RestartMenuLabel(AppContext.BaseDirectory), null, async (_, _) => {restartRequested=true;await QuitAsync();});
                menu.Items.Add("退出新版烛龙", null, async (_, _) => await QuitAsync());
                trayIcon = CreateIcon();
                tray = new Forms.NotifyIcon { Text = "烛龙新版 · 朱印 · 正在采样", Icon = trayIcon, ContextMenuStrip = menu, Visible = true };
                tray.DoubleClick += (_, _) => OpenWindow();
                SystemSnapshot? traySnapshot = null;
                hub.Sampled += value => application.Dispatcher.BeginInvoke(() =>
                {
                    if (!quitting && tray is not null)
                    {
                        traySnapshot = value;
                        tray.Text = FormatTrayTooltip(value, hub.LiveReadout);
                    }
                });
                hub.ReadoutChanged += value => application.Dispatcher.BeginInvoke(() =>
                {
                    if (!quitting && tray is not null && traySnapshot is { } snapshot)
                        tray.Text = FormatTrayTooltip(snapshot, value);
                });
                registration = ThreadPool.RegisterWaitForSingleObject(wake,
                    (_, _) => application.Dispatcher.BeginInvoke(OpenWindow), null, Timeout.Infinite, false);
                hub.Notified += value => application.Dispatcher.BeginInvoke(()=> { if(!quitting&&tray is not null){tray.BalloonTipTitle=value.Title;tray.BalloonTipText=value.Text;tray.BalloonTipIcon=value.Level=="error"?Forms.ToolTipIcon.Error:Forms.ToolTipIcon.Warning;tray.ShowBalloonTip(8000);} });
                hub.Start();
                WriteLifecycle("app_started", new { pid = Environment.ProcessId });
                if(!background)OpenWindow();
            }
            catch (Exception error)
            {
                MessageBox.Show("烛龙未能启动：" + error.Message, "烛龙新版");
                if(error is JsonException or ArgumentException && File.Exists(Path.Combine(directory,"settings.json")))
                    try{new SettingsWindow(directory).ShowDialog();}catch(Exception repair)when(repair is IOException or UnauthorizedAccessException){MessageBox.Show(repair.Message,"设置无法打开");}
                application.Shutdown(1);
            }
        };
        application.SessionEnding += (_, args) => { args.Cancel = false; };
        try { return application.Run(); }
        finally
        {
            registration?.Unregister(null);
            tray?.Dispose();
            trayIcon?.Dispose();
            StartupTasks.WaitForIdleAsync().GetAwaiter().GetResult();
            ClosePortalAsync().GetAwaiter().GetResult();
            if (hub is not null) { hub.StopAsync().GetAwaiter().GetResult(); hub.Dispose(); }
            singleton.ReleaseMutex();
            if(restartRequested)
            {var next=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=true};next.ArgumentList.Add("--data-dir");next.ArgumentList.Add(directory);try{Process.Start(next);}catch(Exception error)when(error is System.ComponentModel.Win32Exception or IOException){WriteLifecycle("restart_failed",error.Message);MessageBox.Show(error.Message,"新版未能重新打开");}}
        }
    }

    internal static string RestartMenuLabel(string programDirectory)
    {
        const string label = "重启当前版本";
        try
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(programDirectory, "runtime-manifest.json"), System.Text.Encoding.UTF8));
            if (manifest.RootElement.ValueKind == JsonValueKind.Object && manifest.RootElement.TryGetProperty("version", out var value) &&
                value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } version)
                return label + "（" + version.Split('-', 2)[0] + "）";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        return label;
    }

    internal static string FormatTrayTooltip(SystemSnapshot value, LiveSystemReadout? readout = null)
    {
        static string Percent(double? number) => number is { } percent ? $"{percent:F0}%" : "—";
        static string Memory(double? percent, double? used, double? total) =>
            $"{Percent(percent)}{(used.HasValue && total.HasValue ? $" ({used:F1}/{total:F1} GB)" : "")}";

        var cpu = readout is null ? value.CpuPercent : readout.CpuPercent;
        var physical = readout is null ? Memory(value.MemoryPercent, value.MemoryUsedGb, value.MemoryTotalGb)
            : Memory(readout.MemoryPercent, readout.MemoryUsedGb, readout.MemoryTotalGb);
        return $"烛龙新版 · 朱印\nCPU {Percent(cpu)}\n物理内存 {physical}\n" +
            $"提交内存 {Memory(value.CommitPercent, value.CommitUsedGb, value.CommitLimitGb)}";
    }

    static void OpenWindow()
    {
        if (quitting || hub is null) return;
        if (window is null)
        {
            window = new DesktopWindow(hub, Path.Combine(AppContext.BaseDirectory, "Web"), Path.Combine(directory, "cache", "WebView"));
            window.Lifecycle += WriteLifecycle;
            window.ExitRequested += async()=>await QuitAsync();
            window.SettingsRequested += async()=>await NativeAsync(()=>{OpenSettings();return Task.CompletedTask;});
            window.AiSettingsRequested += OpenAiSettings;
            window.Closed += (_, _) => window = null;
            window.Show();
        }
        else
        {
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
        }
    }

    static async Task QuitAsync()
    {
        if (quitting) return;
        quitting = true;
        try{File.WriteAllText(Path.Combine(directory,"intentional-exit"),DateTimeOffset.UtcNow.ToString("O"),System.Text.Encoding.UTF8);}
        catch(Exception error)when(error is IOException or UnauthorizedAccessException){WriteLifecycle("exit_intent_failed",error.Message);}
        tray!.Visible = false;
        window?.Close();
        settingsWindow?.Close();
        aiSettingsWindow?.Close();
        try{await StartupTasks.WaitForIdleAsync();await ClosePortalAsync();if(hub is not null)await hub.StopAsync();}
        catch(Exception error)when(error is not(OutOfMemoryException or StackOverflowException)){WriteLifecycle("shutdown_failed",error.Message);}
        finally{WriteLifecycle("app_exited",new{pid=Environment.ProcessId});application!.Shutdown();}
    }

    internal static void WriteLifecycle(string kind, object? detail)
    {
        lock (logGate)
        {
            var path=Path.Combine(directory,"lifecycle.jsonl");
            try
            {
            if(File.Exists(path)&&new FileInfo(path).Length>1024*1024)File.Move(path,path+".previous",true);
            File.AppendAllText(Path.Combine(directory, "lifecycle.jsonl"), JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UtcNow, kind, detail }, Json) + "\n", System.Text.Encoding.UTF8);
            }catch(IOException){}catch(UnauthorizedAccessException){}
        }
    }

    static void ShowActionResults(IReadOnlyList<ActionResult> values,string title)
    {
        if(quitting)return;
        MessageBox.Show(values.Count==0?"当前没有需要更改的目标。":string.Join("\n",values.Select(x=>x.Target+" · "+(x.Status=="throttled"?"已限流":x.Status=="restored"?"已还原":x.Status.StartsWith("degraded:")?"未完整完成":x.Status.StartsWith("blocked:")?"安全检查拒绝":"未执行")+" · "+x.Status+(x.Detail is null?"":" · "+x.Detail))),title);
    }
    static void OpenAiSettings()
    {
        if (quitting) return;
        if (aiSettingsWindow is null)
        {
            aiSettingsWindow = new AiAccountSettingsWindow(directory);
            aiSettingsWindow.Closed += (_, _) => aiSettingsWindow = null;
            aiSettingsWindow.Show();
        }
        else aiSettingsWindow.Activate();
    }

    static void OpenSettings()
    {
        if(quitting)return;
        if(settingsWindow is null){settingsWindow=new SettingsWindow(directory);settingsWindow.Closed+=(_,_)=>settingsWindow=null;settingsWindow.Show();}
        else settingsWindow.Activate();
    }
    static async Task OpenBrowserAsync()
    {
        if(!await portalOperation.WaitAsync(0).ConfigureAwait(false))return;
        try
        {
            if(portal is null)
            {
                var next=new LocalPortal(hub!,Path.Combine(AppContext.BaseDirectory,"Web"),id=>application!.Dispatcher.InvokeAsync(()=>ConfirmPortalAsync(id)).Task.Unwrap());
                try{await next.StartAsync().ConfigureAwait(false);portal=next;}catch{await next.DisposeAsync().ConfigureAwait(false);throw;}
            }
            if(!quitting)Process.Start(new ProcessStartInfo(portal.Address){UseShellExecute=true});
        }
        finally{portalOperation.Release();}
    }
    static async Task<object?> ConfirmPortalAsync(string id)
    {
        var decision=hub!.FindAction(id);if(decision is null)throw new ArgumentException("动作已失效，请刷新告警。");
        if(MessageBox.Show(decision.Title+"\n"+decision.Text+"\n是否执行 "+decision.SuggestedAction+" · "+(decision.Target??decision.ProcessName)+"？","烛龙新版 · 确认处置",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return new{status="cancelled"};
        return await hub.ExecuteConfirmedAsync(id);
    }
    static async Task ClosePortalAsync(){await portalOperation.WaitAsync().ConfigureAwait(false);try{if(portal is not{} current)return;portal=null;await current.DisposeAsync().ConfigureAwait(false);}finally{portalOperation.Release();}}
    static int WriteDiagnostics(string output)
    {
        using var monitor=new SystemMonitor();monitor.Collect();Thread.Sleep(150);var snapshot=monitor.Collect();
        using var process=Process.GetCurrentProcess();File.WriteAllText(Path.GetFullPath(output),JsonSerializer.Serialize(new{timestamp=DateTimeOffset.UtcNow,snapshot,self=new{pid=Environment.ProcessId,privateBytes=process.PrivateMemorySize64,workingSet=process.WorkingSet64,handles=process.HandleCount},scope="read-only native probe; no rules/actions/autostart"},new JsonSerializerOptions(Json){WriteIndented=true}),System.Text.Encoding.UTF8);return 0;
    }
    static int NativeSafety(string helper,string output)
    {
        Directory.CreateDirectory(output);
        try{var assertions=Zhulong.Runtime.Actions.RuntimeSafetyTests.RunNativeAsync(helper,output).GetAwaiter().GetResult();File.WriteAllText(Path.Combine(output,"native-safety.json"),JsonSerializer.Serialize(new{status="PASS",assertions,timestamp=DateTimeOffset.UtcNow},Json));return 0;}
        catch(Exception error){File.WriteAllText(Path.Combine(output,"native-safety.json"),JsonSerializer.Serialize(new{status="FAIL",error=error.ToString(),timestamp=DateTimeOffset.UtcNow},Json));return 1;}
    }
    static async Task NativeAsync(Func<Task> action)
    {if(quitting)return;try{await action();}catch(Exception error)when(error is not(OutOfMemoryException or StackOverflowException)){if(!quitting)MessageBox.Show(error.Message,"烛龙新版 · 操作未完成");else WriteLifecycle("native_action_cancelled",error.Message);}}
    static async Task ImportAsync()
    {
        var choose=new Microsoft.Win32.OpenFileDialog{Title="选择旧烛龙历史库（只读）",Filter="SQLite数据库|*.db;*.sqlite;*.sqlite3|所有文件|*.*"};
        if(choose.ShowDialog()!=true)return;
        if(MessageBox.Show("将只读导入所选旧版库的指标与事件；旧版配置不导入。原始记录保留，旧版无时区时间按本机时区解释。导入时历史查询可能暂时等待。继续？","导入旧版历史",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;
        var result=await hub!.ImportLegacyAsync(choose.FileName);if(!quitting)MessageBox.Show(JsonSerializer.Serialize(result,Json),"历史导入结果");
    }
    static void ExportDiagnostics()
    {
        var save=new Microsoft.Win32.SaveFileDialog{Title="导出新版诊断",Filter="JSON|*.json",FileName="zhulong-diagnostics.json"};
        if(save.ShowDialog()==true)File.WriteAllText(save.FileName,JsonSerializer.Serialize(hub!.Diagnostics(),new JsonSerializerOptions(Json){WriteIndented=true}),System.Text.Encoding.UTF8);
    }
    static async Task ConfigureStartupAsync(StartupTasks.Kind kind)
    {
        var logon = kind == StartupTasks.Kind.Logon;
        var title = logon ? "登录后后台自启" : "5 分钟存活守护";
        var detail = logon ? "Windows 登录后以当前用户权限在托盘后台启动。"
            : "每 5 分钟以当前用户权限检查新版。主动退出会保留退出意图。";
        var enabled = await StartupTasks.IsEnabledAsync(kind);
        if (quitting) return;
        var question = enabled ? "当前已启用。是否关闭" + title + "？" : "是否开启" + title + "？\n" + detail;
        if (MessageBox.Show(question, title, MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        await StartupTasks.SetEnabledAsync(kind, !enabled, directory);
        if (!quitting) MessageBox.Show((enabled ? "已关闭" : "已开启") + title + "，并已回读确认。", "烛龙新版");
    }

    static int ConfigureLogonFromCommandLine(bool enabled)
    {
        using var singleton = new Mutex(true, InstanceMutexName, out var owns);
        if (!owns)
        {
            Console.Error.WriteLine("新版正在运行，请使用托盘中的登录自启设置。");
            return 2;
        }
        try
        {
            StartupTasks.SetEnabledAsync(StartupTasks.Kind.Logon, enabled, Path.Combine(AppContext.BaseDirectory, "data")).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
        finally { singleton.ReleaseMutex(); }
    }

    static System.Drawing.Icon CreateIcon() =>
        new(Path.Combine(AppContext.BaseDirectory, "Assets", "zhulong-next.ico"));
}

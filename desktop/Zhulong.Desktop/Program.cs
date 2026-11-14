using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Forms = System.Windows.Forms;

namespace Zhulong.Desktop;

internal static class Program
{
    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    static Application? application;
    static DataHub? hub;
    static DesktopWindow? window;
    static ResourceDockWindow? resourceDock;
    static ZhulongContextMenu? trayMenu;
    static SystemSnapshot? traySnapshot;
    static Forms.NotifyIcon? tray;
    static System.Drawing.Icon? trayIcon;
    static string directory = "";
    static bool quitting;
    static bool restartRequested;
    static bool preparingUpdate;
    static bool acceleratingMemory;
    static bool checkingUpdate;
    static LocalPortal? portal;
    static SettingsWindow? settingsWindow;
    static AiAccountSettingsWindow? aiSettingsWindow;
    static readonly object logGate = new();
    static readonly SemaphoreSlim portalOperation=new(1,1);
    static readonly CancellationTokenSource shutdown = new();
    const string InstanceMutexName = "Local\\ZhulongNextInstance";

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--apply-update") return UpdateApply.Run(args[1]);
        if (args.Length == 2 && args[0] == "--self-test") return DesktopSelfTests.Run(args[1]);
        if(args.Length==3&&args[0]=="--self-test-native")return NativeSafety(args[1],args[2]);
        if(args.Length==3&&args[0]=="--runtime-probe")return RuntimeProbe.Run(args[1],int.Parse(args[2]));
        if(args.Length==1&&args[0]=="--uninstall-tasks"){StartupTasks.RemoveOwnedAsync(Path.Combine(AppContext.BaseDirectory,"data")).GetAwaiter().GetResult();return 0;}
        if(args.Length==2&&args[0]=="--diagnose")return WriteDiagnostics(args[1]);
        if (args.Length > 0 && (args[0] == "--enable-logon-startup" || args[0] == "--disable-logon-startup"))
            return args.Length == 1 ? ConfigureLogonFromCommandLine(args[0] == "--enable-logon-startup") : 2;
        if (UpdateApply.IsApplying(AppContext.BaseDirectory)) return 0;
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
        if (UpdateApply.IsApplying(AppContext.BaseDirectory))
        {
            singleton.ReleaseMutex();
            return 0;
        }
        if(!args.Contains("--watchdog"))File.Delete(suppress);
        application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        RegisteredWaitHandle? registration = null;
        application.Startup += (_, _) =>
        {
            try
            {
                hub = new DataHub(directory);
                trayIcon = CreateIcon();
                tray = new Forms.NotifyIcon { Text = "烛龙 · 正在采样", Icon = trayIcon, Visible = true };
                tray.MouseUp += (_, args) =>
                {
                    if (args.Button == Forms.MouseButtons.Right) OpenTrayMenu();
                };
                tray.MouseDoubleClick += (_, args) =>
                {
                    if (args.Button == Forms.MouseButtons.Left) OpenWindow();
                };
                hub.Sampled += value => application.Dispatcher.BeginInvoke(() =>
                {
                    if (!quitting && tray is not null)
                    {
                        traySnapshot = value;
                        tray.Text = FormatTrayTooltip(value, hub.LiveReadout);
                        resourceDock?.ApplySample(value);
                    }
                });
                hub.ReadoutChanged += value => application.Dispatcher.BeginInvoke(() =>
                {
                    if (!quitting && tray is not null && traySnapshot is { } snapshot)
                        tray.Text = FormatTrayTooltip(snapshot, value);
                    if (!quitting) resourceDock?.ApplyLive(value);
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

    static void OpenTrayMenu()
    {
        if (quitting || trayMenu?.IsOpen == true) return;
        var menu = new ZhulongContextMenu();
        trayMenu = menu;
        menu.Add("打开烛龙", OpenWindow);
        menu.Add("显示桌面贴边挂件", async () => await NativeAsync(Synchronous(ToggleDock)), resourceDock is not null);
        menu.Add("整理内存", async () => await AccelerateMemoryAsync(null)).IsEnabled = !acceleratingMemory;
        menu.Items.Add(new Separator());
        menu.Add("设置", async () => await NativeAsync(Synchronous(OpenSettings)));
        menu.Add(RestartMenuLabel(AppContext.BaseDirectory), async () =>
        {
            if (preparingUpdate) return;
            restartRequested = true;
            await QuitAsync();
        });
        menu.Add("退出烛龙", async () => await QuitAsync());
        menu.Closed += (_, _) => { if (ReferenceEquals(trayMenu, menu)) trayMenu = null; };
        menu.OpenAtPointer();
    }

    static void ToggleDock()
    {
        if (resourceDock is { } existing)
        {
            existing.Close();
            return;
        }
        var dock = new ResourceDockWindow();
        dock.Closed += (_, _) => resourceDock = null;
        dock.OpenRequested += OpenWindow;
        dock.HideRequested += dock.Close;
        dock.AccelerateRequested += async () => await AccelerateMemoryAsync(dock);
        dock.ApplySample(traySnapshot);
        dock.ApplyLive(hub!.LiveReadout);
        dock.Show();
        resourceDock = dock;
    }

    internal static string RestartMenuLabel(string programDirectory)
    {
        const string label = "重启当前版本";
        try
        {
            if (ReadProgramVersion(programDirectory) is { Length: > 0 } version)
                return label + "（" + version.Split('-', 2)[0] + "）";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        return label;
    }

    static string? ReadProgramVersion(string programDirectory)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(programDirectory, "runtime-manifest.json"), System.Text.Encoding.UTF8));
        return manifest.RootElement.ValueKind == JsonValueKind.Object && manifest.RootElement.TryGetProperty("version", out var value) &&
            value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    static async Task CheckUpdateAsync()
    {
        if (checkingUpdate) return;
        checkingUpdate = true;
        try { await CheckUpdateCoreAsync(); }
        finally { checkingUpdate = false; }
    }

    static async Task CheckUpdateCoreAsync()
    {
        const string title = "烛龙更新";
        var programDirectory = AppContext.BaseDirectory;
        var version = ReadProgramVersion(programDirectory) ?? throw new InvalidDataException("无法读取当前运行版本。");
        var installed = File.Exists(Path.Combine(programDirectory, "installation.json"));
        var cancellationToken = shutdown.Token;
        var package = await UpdateDownload.CheckAsync(version, installed, cancellationToken);
        if (quitting) return;
        if (package is null)
        {
            MessageBox.Show($"当前版本 {version}，暂无可用的正式更新。", title);
            return;
        }
        if (MessageBox.Show($"当前版本 {version}\n可用更新 {package.Version}（{package.Size / 1048576.0:F1} MB）\n\n下载到程序目录的 updates？",
                title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        tray!.BalloonTipTitle = title;
        tray.BalloonTipText = $"正在下载 {package.Version}，可继续使用烛龙。";
        tray.ShowBalloonTip(3500);
        var path = await UpdateDownload.StageAsync(package, programDirectory, cancellationToken);
        if (quitting || MessageBox.Show($"已下载并校验 {package.Version}。\n现在应用更新？后台采样将短暂停止。",
                title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        preparingUpdate = true;
        try { await UpdateApply.StartAsync(programDirectory, directory, package, path, cancellationToken); }
        finally { preparingUpdate = false; }
        await QuitAsync();
    }

    internal static string FormatTrayTooltip(SystemSnapshot value, LiveSystemReadout? readout = null)
    {
        static string Percent(double? number) => number is { } percent ? $"{percent:F0}%" : "—";
        static string Memory(double? percent, double? used, double? total) =>
            $"{Percent(percent)}{(used.HasValue && total.HasValue ? $" ({used:F1}/{total:F1} GB)" : "")}";

        var cpu = readout is null ? value.CpuPercent : readout.CpuPercent;
        var physical = readout is null ? Memory(value.MemoryPercent, value.MemoryUsedGb, value.MemoryTotalGb)
            : Memory(readout.MemoryPercent, readout.MemoryUsedGb, readout.MemoryTotalGb);
        return $"烛龙\nCPU {Percent(cpu)}\n物理内存 {physical}\n" +
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
        if (quitting || preparingUpdate) return;
        quitting = true;
        shutdown.Cancel();
        try{File.WriteAllText(Path.Combine(directory,"intentional-exit"),DateTimeOffset.UtcNow.ToString("O"),System.Text.Encoding.UTF8);}
        catch(Exception error)when(error is IOException or UnauthorizedAccessException){WriteLifecycle("exit_intent_failed",error.Message);}
        tray!.Visible = false;
        if (trayMenu is { } menu) menu.IsOpen = false;
        resourceDock?.Close();
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

    static async Task AccelerateMemoryAsync(ResourceDockWindow? dock)
    {
        if (quitting || acceleratingMemory) return;
        acceleratingMemory = true;
        void ShowResult(string status)
        {
            if (quitting) return;
            if (dock is not null && resourceDock == dock) dock.SetAccelerationStatus(status);
            else if (dock is null && tray is { } icon)
            {
                icon.BalloonTipTitle = "烛龙 · 整理内存";
                icon.BalloonTipText = status;
                icon.BalloonTipIcon = Forms.ToolTipIcon.Info;
                icon.ShowBalloonTip(3500);
            }
        }
        try
        {
            dock?.SetAccelerationStatus("整理中…");
            var results = await hub!.TrimWorkingSetsAsync();
            ShowResult(FormatMemoryAcceleration(results));
        }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
        {
            ShowResult(error is InvalidOperationException ? "已有操作，请稍候" : "整理未完成");
            WriteLifecycle("dock_acceleration_failed", error.Message);
        }
        finally { acceleratingMemory = false; }
    }

    internal static string FormatMemoryAcceleration(IReadOnlyList<ActionResult> results)
    {
        var measured = results.Where(item => item.PrivateWorkingSetReductionBytes.HasValue).ToArray();
        var degraded = results.Any(item => item.Status.StartsWith("degraded:", StringComparison.Ordinal) && item.Status != "degraded:frequency_cap");
        if (measured.Length > 0)
        {
            var bytes = measured.Sum(item => item.PrivateWorkingSetReductionBytes!.Value);
            var amount = bytes >= 1073741824 ? $"{bytes / 1073741824d:F2} GiB" : $"{bytes / 1048576d:F1} MiB";
            var incomplete = degraded || results.Any(item => item.Status == "trimmed" && !item.PrivateWorkingSetReductionBytes.HasValue);
            return $"{(incomplete ? "已测部分" : bytes == 0 ? "下降" : "已释放")}\n{amount}";
        }
        if (results.Any(item => item.Status == "trimmed")) return "已整理\n变化不可测";
        if (degraded) return "整理未完整完成";
        if (results.Any(item => item.Status == "degraded:frequency_cap")) return "刚整理过\n请稍后再试";
        return results.Count == 0 ? "未配置整理目标" : "无可整理后台进程";
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
        if(settingsWindow is not null) { settingsWindow.Activate(); return; }
        (string Group, string Label, Func<Task> Run)[] tools =
        [
            ("监控与提醒", "暂停自动处置与提醒 5 分钟", Synchronous(hub!.PauseFiveMinutes)),
            ("监控与提醒", "静音提醒 5 分钟", Synchronous(hub.MuteFiveMinutes)),
            ("监控与提醒", "恢复自动处置与提醒", Synchronous(hub.Resume)),
            ("监控与提醒", "切换限流", async () => ShowActionResults(await hub.ToggleThrottleAsync(), "限流结果")),
            ("监控与提醒", "恢复默认限流规则", async () => ShowActionResults(await hub.ResetThrottleAsync(), "恢复默认规则结果")),
            ("入口与启动", "在浏览器打开烛龙", OpenBrowserAsync),
            ("入口与启动", "关闭浏览器入口", ClosePortalAsync),
            ("入口与启动", "登录后后台自启…", () => ConfigureStartupAsync(StartupTasks.Kind.Logon)),
            ("入口与启动", "5 分钟存活守护…", () => ConfigureStartupAsync(StartupTasks.Kind.Watchdog)),
            ("账户与维护", "AI 账户", Synchronous(OpenAiSettings)),
            ("账户与维护", "导入旧版历史…", ImportAsync),
            ("账户与维护", "导出诊断…", Synchronous(ExportDiagnostics)),
            ("账户与维护", "检查更新…", CheckUpdateAsync)
        ];
        settingsWindow = new SettingsWindow(directory,
            tools.Select(tool => (tool.Group, tool.Label, (Func<Task>)(() => NativeAsync(tool.Run)))).ToArray());
        settingsWindow.Closed += (_, _) => settingsWindow = null;
        settingsWindow.Show();
    }

    static Func<Task> Synchronous(Action action) => () => { action(); return Task.CompletedTask; };
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
            Console.Error.WriteLine("烛龙正在运行，请打开设置里的工具与维护，管理登录自启。");
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

    internal static System.Drawing.Icon CreateIcon() =>
        new(Path.Combine(AppContext.BaseDirectory, "Assets", "zhulong-next.ico"));
}

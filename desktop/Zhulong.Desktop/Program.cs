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
    static readonly object logGate = new();

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--self-test") return DesktopSelfTests.Run(args[1]);
        directory = args.Length == 2 && args[0] == "--data-dir" ? Path.GetFullPath(args[1]) : Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(directory);
        // 新版使用独立名字，不接管或改动旧烛龙。
        using var wake = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\ZhulongNextOpen");
        using var singleton = new Mutex(true, "Local\\ZhulongNextInstance", out var first);
        if (!first) { wake.Set(); return 0; }
        application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        RegisteredWaitHandle? registration = null;
        application.Startup += (_, _) =>
        {
            try
            {
                hub = new DataHub(directory);
                var menu = new Forms.ContextMenuStrip();
                menu.Items.Add("打开烛龙新版（朱印）", null, (_, _) => OpenWindow());
                menu.Items.Add("退出新版烛龙", null, async (_, _) => await QuitAsync());
                trayIcon = CreateIcon();
                tray = new Forms.NotifyIcon { Text = "烛龙新版 · 朱印 · 正在采样", Icon = trayIcon, ContextMenuStrip = menu, Visible = true };
                tray.DoubleClick += (_, _) => OpenWindow();
                hub.Sampled += value => application.Dispatcher.BeginInvoke(() =>
                {
                    if (!quitting && tray is not null)
                        tray.Text = "烛龙新版 · 朱印 · 提交 " + (value.CommitPercent?.ToString("F0") ?? "—") + "%";
                });
                registration = ThreadPool.RegisterWaitForSingleObject(wake,
                    (_, _) => application.Dispatcher.BeginInvoke(OpenWindow), null, Timeout.Infinite, false);
                hub.Start();
                WriteLifecycle("app_started", new { pid = Environment.ProcessId });
                OpenWindow();
            }
            catch (Exception error)
            {
                MessageBox.Show("烛龙未能启动：" + error.Message, "烛龙新版");
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
            if (hub is not null) { hub.StopAsync().GetAwaiter().GetResult(); hub.Dispose(); }
            singleton.ReleaseMutex();
        }
    }

    static void OpenWindow()
    {
        if (quitting || hub is null) return;
        if (window is null)
        {
            window = new DesktopWindow(hub, Path.Combine(AppContext.BaseDirectory, "Web"), Path.Combine(directory, "cache", "WebView"));
            window.Lifecycle += WriteLifecycle;
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
        tray!.Visible = false;
        window?.Close();
        if (hub is not null) await hub.StopAsync();
        WriteLifecycle("app_exited", new { pid = Environment.ProcessId });
        application!.Shutdown();
    }

    internal static void WriteLifecycle(string kind, object? detail)
    {
        lock (logGate)
            File.AppendAllText(Path.Combine(directory, "lifecycle.jsonl"), JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UtcNow, kind, detail }, Json) + "\n", System.Text.Encoding.UTF8);
    }

    static System.Drawing.Icon CreateIcon() =>
        new(Path.Combine(AppContext.BaseDirectory, "Assets", "zhulong-next.ico"));
}

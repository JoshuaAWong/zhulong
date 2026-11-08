using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace Zhulong.Desktop;

internal static class Program
{
    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    static Application? application;
    static DataHub? hub;
    static DesktopWindow? window;
    static Forms.NotifyIcon? tray;
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
                menu.Items.Add("打开烛龙", null, (_, _) => OpenWindow());
                menu.Items.Add("退出新版烛龙", null, async (_, _) => await QuitAsync());
                tray = new Forms.NotifyIcon { Text = "烛龙新版 · 正在采样", Icon = CreateIcon(), ContextMenuStrip = menu, Visible = true };
                tray.DoubleClick += (_, _) => OpenWindow();
                hub.Sampled += value => application.Dispatcher.BeginInvoke(() =>
                {
                    if (!quitting && tray is not null)
                        tray.Text = "烛龙新版 · 提交 " + (value.CommitPercent?.ToString("F0") ?? "—") + "%";
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

    static System.Drawing.Icon CreateIcon()
    {
        const string shape = "M71 23C58 9 35 12 22 29C8 48 18 76 42 83C62 90 81 77 83 60C84 44 72 36 60 39C47 42 44 54 51 61C55 66 62 66 68 61C57 64 52 58 55 51C59 41 72 46 72 58C72 70 58 77 44 72C26 67 20 49 28 35C37 20 56 17 67 28L64 33Q76 32 84 25L76 22L78 17L70 21Z";
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.PushTransform(new ScaleTransform(.64, .64));
            drawing.DrawGeometry(new SolidColorBrush(Color.FromRgb(144, 102, 63)), null, Geometry.Parse(shape));
        }
        var bitmap = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream(); encoder.Save(stream); stream.Position = 0;
        using var raster = new System.Drawing.Bitmap(stream);
        var handle = raster.GetHicon();
        try { using var icon = System.Drawing.Icon.FromHandle(handle); return (System.Drawing.Icon)icon.Clone(); }
        finally { DestroyIcon(handle); }
    }
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);
}

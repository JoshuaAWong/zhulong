using System.IO;
using System.Diagnostics;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Zhulong.P0;

sealed class WebPanel(string assets, string profile) : IPanel
{
    readonly Window window = new()
    {
        Title = "烛龙 P0 · WebView2 + React 对照", Width = 1000, Height = 720,
        Left = 100, Top = 100, ShowActivated = false, ShowInTaskbar = false
    };
    WebView2? view;
    CoreWebView2Environment? environment;
    readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    Process? browserProcess;
    TaskCompletionSource? painted;
    int sequence = -1;
    bool closed;

    public async Task OpenAsync()
    {
        if (!File.Exists(Path.Combine(assets, "index.html")))
            throw new FileNotFoundException("P0 前端资源缺失，不能执行混合面板测试");
        view = new WebView2();
        window.Content = view;
        window.Show();
        environment = await CoreWebView2Environment.CreateAsync(null, profile);
        await view.EnsureCoreWebView2Async(environment);
        browserProcess = Process.GetProcessById((int)view.CoreWebView2.BrowserProcessId);
        _ = browserProcess.SafeHandle; // 固定进程实例，后续不按可能复用的 PID 猜测退出。
        Program.Emit(new { kind = "webview", version = environment.BrowserVersionString,
            browserPid = view.CoreWebView2.BrowserProcessId });
        view.CoreWebView2.SetVirtualHostNameToFolderMapping("p0.zhulong.invalid", assets,
            CoreWebView2HostResourceAccessKind.DenyCors);
        view.CoreWebView2.Settings.AreDevToolsEnabled = false;
        view.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        view.CoreWebView2.Settings.IsStatusBarEnabled = false;
        view.CoreWebView2.NavigationStarting += OnNavigation;
        view.CoreWebView2.NewWindowRequested += OnNewWindow;
        view.CoreWebView2.WebMessageReceived += OnMessage;
        view.Source = new Uri("https://p0.zhulong.invalid/index.html");
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    static void OnNavigation(object? sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (args.Uri != "https://p0.zhulong.invalid/index.html") args.Cancel = true;
    }
    static void OnNewWindow(object? sender, CoreWebView2NewWindowRequestedEventArgs args) => args.Handled = true;

    void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (args.Source != "https://p0.zhulong.invalid/index.html") return;
        string message = args.TryGetWebMessageAsString();
        if (message == "ready") ready.TrySetResult();
        if (message == $"paint:{sequence}") painted?.TrySetResult();
    }

    public async Task RenderAsync(Snapshot snapshot)
    {
        sequence = snapshot.Sequence;
        painted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        view!.CoreWebView2.PostWebMessageAsJson(Program.Json(snapshot));
        await painted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        painted = null;
    }

    public async Task CaptureAsync(string path)
    {
        using var stream = File.Create(path);
        await view!.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
    }

    public async Task CloseAsync()
    {
        if (closed) return;
        closed = true;
        try
        {
            if (view?.CoreWebView2 is { } core)
            {
                core.WebMessageReceived -= OnMessage;
                core.NavigationStarting -= OnNavigation;
                core.NewWindowRequested -= OnNewWindow;
            }
            window.Content = null;
            view?.Dispose();
            view = null;
            window.Close();
            if (browserProcess is not null)
                await browserProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            browserProcess?.Dispose();
            browserProcess = null;
            environment = null;
        }
    }
}

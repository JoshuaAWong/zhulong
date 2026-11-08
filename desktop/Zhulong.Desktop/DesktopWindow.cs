using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Zhulong.Desktop;

internal sealed class DesktopWindow : Window
{
    const string Address = "https://desktop.zhulong.invalid/index.html";
    readonly DataHub hub;
    readonly string webDirectory, profileDirectory, sessionToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    WebView2? view;
    bool closed;
    public uint? BrowserPid { get; private set; }
    public event Action<string, object?>? Lifecycle;

    public DesktopWindow(DataHub hub, string webDirectory, string profileDirectory)
    {
        this.hub = hub;
        this.webDirectory = webDirectory;
        this.profileDirectory = profileDirectory;
        Title = "烛龙 · 水墨观察版";
        Width = 1440; Height = 960; MinWidth = 1000; MinHeight = 680;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = System.Windows.Media.Brushes.WhiteSmoke;
        Loaded += async (_, _) => await InitializeAsync();
        Closed += (_, _) => DisposeView();
    }

    async Task InitializeAsync()
    {
        try
        {
            if (!File.Exists(Path.Combine(webDirectory, "index.html")))
                throw new FileNotFoundException("界面资源缺失，请重新解压完整程序包。");
            view = new WebView2();
            Content = view;
            var environment = await CoreWebView2Environment.CreateAsync(null, profileDirectory);
            if (closed) return;
            await view.EnsureCoreWebView2Async(environment);
            if (closed) return;
            var core = view.CoreWebView2;
            BrowserPid = core.BrowserProcessId;
            core.SetVirtualHostNameToFolderMapping("desktop.zhulong.invalid", webDirectory, CoreWebView2HostResourceAccessKind.DenyCors);
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsWebMessageEnabled = true;
            core.NavigationStarting += NavigationStarting;
            core.NewWindowRequested += NewWindowRequested;
            core.PermissionRequested += PermissionRequested;
            core.WebMessageReceived += MessageReceived;
            core.DownloadStarting += DownloadStarting;
            view.Source = new Uri(Address);
            Lifecycle?.Invoke("webview_opened", new { browserPid = BrowserPid });
        }
        catch (Exception error) when (!closed)
        {
            Content = new System.Windows.Controls.TextBlock
            {
                Text = "桌面界面未能启动：" + error.Message + "\n请确认已安装 Microsoft Edge WebView2 Runtime。",
                Margin = new Thickness(36), TextWrapping = TextWrapping.Wrap
            };
            Lifecycle?.Invoke("webview_failed", error.Message);
        }
        catch (Exception) when (closed) { }
    }

    internal static bool IsTrustedSource(string source) =>
        source.Split('#')[0] == Address;

    static void NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs args)
    { if (!IsTrustedSource(args.Uri)) args.Cancel = true; }
    static void NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs args) => args.Handled = true;
    static void DownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs args) => args.Cancel = true;
    static void PermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs args) => args.State = CoreWebView2PermissionState.Deny;

    async void MessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (!IsTrustedSource(args.Source) || closed) return;
        int? id = null;
        try
        {
            using var document = JsonDocument.Parse(args.WebMessageAsJson);
            var message = document.RootElement;
            var kind = message.GetProperty("kind").GetString();
            if (kind == "ready") { Reply(new { kind = "init", token = sessionToken }); return; }
            if (kind != "query" || message.GetProperty("token").GetString() != sessionToken) return;
            id = message.GetProperty("id").GetInt32();
            var api = message.GetProperty("api").GetString()!;
            var metric = message.TryGetProperty("metric", out var metricValue) ? metricValue.GetString() : null;
            var hours = message.TryGetProperty("hours", out var hoursValue) ? hoursValue.GetInt32() : 24;
            var result = await hub.QueryAsync(api, metric, hours);
            Reply(new { id, result });
        }
        catch (OperationCanceledException) { if (!closed && id.HasValue) Reply(new { id, error = "读取已取消" }); }
        catch (Exception error) when (error is FormatException or JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException or Microsoft.Data.Sqlite.SqliteException or IOException)
        { if (id.HasValue) Reply(new { id, error = error.Message }); }
    }

    void Reply(object message)
    {
        if (!closed && view?.CoreWebView2 is { } core)
            core.PostWebMessageAsJson(JsonSerializer.Serialize(message, Program.Json));
    }

    void DisposeView()
    {
        if (closed) return;
        closed = true;
        if (view?.CoreWebView2 is { } core)
        {
            core.NavigationStarting -= NavigationStarting;
            core.NewWindowRequested -= NewWindowRequested;
            core.PermissionRequested -= PermissionRequested;
            core.WebMessageReceived -= MessageReceived;
            core.DownloadStarting -= DownloadStarting;
        }
        Content = null;
        view?.Dispose();
        view = null;
        Lifecycle?.Invoke("window_closed", new { browserPid = BrowserPid });
    }
}

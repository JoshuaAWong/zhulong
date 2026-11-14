using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Zhulong.Desktop;

internal sealed class DesktopWindow : Window
{
    const string Address = "https://desktop.zhulong.invalid/index.html";
    const int BorderColor = 34;
    const int NonClientLeftDown = 0x00a1, CaptionHit = 2, LeftButton = 1;
    readonly DataHub hub;
    readonly string webDirectory, profileDirectory, sessionToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    readonly Grid frame = new();
    readonly ContentControl page = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    readonly Thickness resizeBorder = new(5);
    WebView2? view;
    HwndSource? source;
    Brush captionInk = SystemColors.WindowTextBrush;
    bool closed, pageReady, readoutSubscribed, nativeDrag;
    public uint? BrowserPid { get; private set; }
    public event Action<string, object?>? Lifecycle;
    public event Action? ExitRequested;
    public event Action? SettingsRequested;
    public event Action? AiSettingsRequested;

    public DesktopWindow(DataHub hub, string webDirectory, string profileDirectory)
    {
        this.hub = hub;
        this.webDirectory = webDirectory;
        this.profileDirectory = profileDirectory;
        Title = "烛龙新版 · 朱印";
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "zhulong-next.ico")));
        Width = 1440; Height = 960; MinWidth = 500; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        UseLayoutRounding = true;
        page.Margin = resizeBorder;
        frame.Children.Add(page);
        Content = frame;
        ApplyFrameTheme("light");
        SourceInitialized += (_, _) =>
        {
            source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                CaptionHeight = 0,
                ResizeBorderThickness = resizeBorder,
                GlassFrameThickness = new Thickness(0),
                CornerRadius = new CornerRadius(8),
                UseAeroCaptionButtons = false
            });
            ApplyFrameTheme("light");
        };
        StateChanged += (_, _) => UpdateWindowState();
        IsVisibleChanged += (_, _) => UpdateReadoutSubscription();
        PreviewKeyDown += (_,args) => {if(args.Key==System.Windows.Input.Key.Q&&(System.Windows.Input.Keyboard.Modifiers&System.Windows.Input.ModifierKeys.Control)!=0){args.Handled=true;ExitRequested?.Invoke();}
            else if(args.Key==System.Windows.Input.Key.OemComma&&(System.Windows.Input.Keyboard.Modifiers&System.Windows.Input.ModifierKeys.Control)!=0){args.Handled=true;SettingsRequested?.Invoke();}};
        Loaded += async (_, _) => await InitializeAsync();
        Closed += (_, _) =>
        {
            DisposeView();
            source = null;
        };
    }

    void ToggleMaximize()
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    string WindowMode => WindowState == WindowState.Maximized ? "maximized" : "normal";

    void UpdateWindowState()
    {
        var maximized = WindowState == WindowState.Maximized;
        frame.Margin = maximized ? SystemParameters.WindowResizeBorderThickness : new Thickness(0);
        page.Margin = maximized ? new Thickness(0) : resizeBorder;
        if (pageReady) Reply(new { kind = "window-state", state = WindowMode });
        UpdateReadoutSubscription();
    }

    void RunWindowCommand(string? command)
    {
        if (command is not ("minimize" or "toggle-maximize" or "close" or "drag")) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (closed || !pageReady) return;
            switch (command)
            {
                case "minimize": SystemCommands.MinimizeWindow(this); break;
                case "toggle-maximize": ToggleMaximize(); break;
                case "close": Close(); break;
                case "drag" when !nativeDrag: BeginFallbackDrag(); break;
            }
        }));
    }

    void BeginFallbackDrag()
    {
        // 兼容旧 WebView2。只在真实鼠标按住时交给系统移动当前窗口。
        if (!IsActive || source is not { IsDisposed: false } || (GetAsyncKeyState(LeftButton) & 0x8000) == 0 || !GetCursorPos(out var cursor)) return;
        ReleaseCapture();
        var position = unchecked((int)((uint)(ushort)cursor.X | ((uint)(ushort)cursor.Y << 16)));
        SendMessage(source.Handle, NonClientLeftDown, new IntPtr(CaptionHit), new IntPtr(position));
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ScreenPoint { public int X, Y; }

    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetCursorPos(out ScreenPoint point);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool ReleaseCapture();
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    bool CanPublishReadout => !closed && pageReady && IsVisible && WindowState != WindowState.Minimized;

    void UpdateReadoutSubscription()
    {
        bool wanted = CanPublishReadout;
        if (wanted == readoutSubscribed) return;
        readoutSubscribed = wanted;
        if (wanted)
        {
            hub.ReadoutChanged += OnReadoutChanged;
            if (hub.LiveReadout is { } current) PublishReadout(current);
        }
        else hub.ReadoutChanged -= OnReadoutChanged;
    }

    void OnReadoutChanged(LiveSystemReadout value)
    {
        if (Dispatcher.CheckAccess()) PublishReadout(value);
        else Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => PublishReadout(value)));
    }

    void PublishReadout(LiveSystemReadout value)
    {
        if (CanPublishReadout) Reply(new { kind = "live-readout", value });
    }

    async Task InitializeAsync()
    {
        try
        {
            if (!File.Exists(Path.Combine(webDirectory, "index.html")))
                throw new FileNotFoundException("界面资源缺失，请重新解压完整程序包。");
            view = new WebView2();
            page.Content = view;
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
            try
            {
                core.Settings.IsNonClientRegionSupportEnabled = true;
                nativeDrag = true;
            }
            catch (Exception error) when (error is NotImplementedException or COMException)
            {
                nativeDrag = false;
            }
            core.NavigationStarting += NavigationStarting;
            core.NewWindowRequested += NewWindowRequested;
            core.PermissionRequested += PermissionRequested;
            core.WebMessageReceived += MessageReceived;
            core.DownloadStarting += DownloadStarting;
            core.ProcessFailed += ProcessFailed;
            view.Source = new Uri(Address);
            Lifecycle?.Invoke("webview_opened", new { browserPid = BrowserPid });
        }
        catch (Exception error) when (!closed)
        {
            page.Content = new TextBlock
            {
                Text = "桌面界面未能启动：" + error.Message + "\n请确认已安装 Microsoft Edge WebView2 Runtime。",
                Foreground = captionInk, Margin = new Thickness(36), TextWrapping = TextWrapping.Wrap
            };
            Lifecycle?.Invoke("webview_failed", error.Message);
        }
        catch (Exception) when (closed) { }
    }

    internal static bool IsTrustedSource(string source) =>
        source.Split('#')[0] == Address;

    void ApplyFrameTheme(string? theme)
    {
        if (theme is not ("light" or "dark")) return;
        var dark = theme == "dark";
        var contrast = SystemParameters.HighContrast;
        Brush background = contrast ? SystemColors.WindowBrush : Palette(dark ? 0x111f24 : 0xf6f7f2);
        captionInk = contrast ? SystemColors.WindowTextBrush : Palette(dark ? 0xe0e8df : 0x273d37);
        Background = frame.Background = background;
        if (!contrast && source is { IsDisposed: false } && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            SetFrameColor(source.Handle, BorderColor, dark ? 0x2b3f42 : 0xd6dfd5);
    }

    static SolidColorBrush Palette(int rgb) => new(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));

    static void SetFrameColor(IntPtr handle, int attribute, int rgb)
    {
        // DWM 使用 COLORREF，字节顺序是红、绿、蓝。
        var colorRef = ((rgb & 0xff) << 16) | (rgb & 0xff00) | (rgb >> 16);
        DwmSetWindowAttribute(handle, attribute, ref colorRef, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);

    void ProcessFailed(object? sender,CoreWebView2ProcessFailedEventArgs args)
    {
        Dispatcher.BeginInvoke(()=>{if(closed)return;Lifecycle?.Invoke("webview_process_failed",args.ProcessFailedKind.ToString());DisposeView();page.Content=new TextBlock{Text="界面进程已停止，后台仍继续采样。请关闭窗口后从朱印托盘重新打开。",Foreground=captionInk,Margin=new Thickness(36),TextWrapping=TextWrapping.Wrap};});
    }

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
            if (kind == "ready")
            {
                pageReady = true;
                Reply(new { kind = "init", token = sessionToken, windowState = WindowMode, nativeDrag });
                UpdateReadoutSubscription();
                return;
            }
            if (message.GetProperty("token").GetString() != sessionToken) return;
            if (kind == "theme") { ApplyFrameTheme(message.GetProperty("theme").GetString()); return; }
            if (kind == "window-command") { RunWindowCommand(message.GetProperty("command").GetString()); return; }
            if (kind != "query") return;
            id = message.GetProperty("id").GetInt32();
            var api = message.GetProperty("api").GetString()!;
            var metric = message.TryGetProperty("metric", out var metricValue) ? metricValue.GetString() : null;
            var hours = message.TryGetProperty("hours", out var hoursValue) ? hoursValue.GetInt32() : 24;
            object? result;
            if(api=="show-settings"){SettingsRequested?.Invoke();result=new{status="opened"};}
            else if (api == "show-ai-settings") { AiSettingsRequested?.Invoke(); result = new { status = "opened" }; }
            else if(api=="action")
            {
                var decision=metric is null?null:hub.FindAction(metric);
                if(decision is null)throw new ArgumentException("动作已失效，请刷新告警。");
                if(MessageBox.Show(this,decision.Title+"\n"+decision.Text+"\n动作："+decision.SuggestedAction+" · "+(decision.Target??decision.ProcessName)+"\n将重新核对水位、进程身份、路径和频次。是否执行？","烛龙新版 · 确认处置",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)result=new {status="cancelled"};
                else result=await hub.ExecuteConfirmedAsync(metric!);
            }
            else result=await hub.QueryAsync(api,metric,hours);
            Reply(new { id, result });
        }
        catch (OperationCanceledException) { if (!closed && id.HasValue) Reply(new { id, error = "读取已取消" }); }
        catch (Exception error) when (error is InvalidDataException or FormatException or JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException or Microsoft.Data.Sqlite.SqliteException or IOException)
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
        pageReady = false;
        UpdateReadoutSubscription();
        if (view?.CoreWebView2 is { } core)
        {
            core.NavigationStarting -= NavigationStarting;
            core.NewWindowRequested -= NewWindowRequested;
            core.PermissionRequested -= PermissionRequested;
            core.WebMessageReceived -= MessageReceived;
            core.DownloadStarting -= DownloadStarting;
            core.ProcessFailed -= ProcessFailed;
        }
        page.Content = null;
        view?.Dispose();
        view = null;
        Lifecycle?.Invoke("window_closed", new { browserPid = BrowserPid });
    }
}

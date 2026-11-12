using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
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
    const int NonClientHitTest = 0x0084, NonClientMouseMove = 0x00a0, NonClientMouseLeave = 0x02a2;
    const int NonClientLeftDown = 0x00a1, NonClientLeftUp = 0x00a2, MaxButtonHit = 9;
    const double TitleHeight = 40;
    readonly DataHub hub;
    readonly string webDirectory, profileDirectory, sessionToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    readonly Grid frame = new();
    readonly DockPanel titleBar = new();
    readonly ContentControl page = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    readonly TextBlock windowTitle = new();
    readonly Button minimizeButton, maximizeButton, closeButton;
    readonly Thickness resizeBorder = SystemParameters.WindowResizeBorderThickness;
    WebView2? view;
    HwndSource? source;
    Brush captionInk = SystemColors.WindowTextBrush, captionHover = SystemColors.ControlLightBrush;
    bool closed, pageReady, readoutSubscribed, maxHover, maxPressed;
    public uint? BrowserPid { get; private set; }
    public event Action<string, object?>? Lifecycle;
    public event Action? ExitRequested;
    public event Action? SettingsRequested;

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
        minimizeButton = CaptionButton("−", "最小化", () => SystemCommands.MinimizeWindow(this));
        maximizeButton = CaptionButton("□", "最大化", ToggleMaximize);
        closeButton = CaptionButton("×", "关闭窗口并留在托盘", Close);
        BuildFrame();
        SourceInitialized += (_, _) =>
        {
            source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                CaptionHeight = Math.Max(0, TitleHeight - resizeBorder.Top),
                ResizeBorderThickness = resizeBorder,
                GlassFrameThickness = new Thickness(0),
                CornerRadius = new CornerRadius(8),
                UseAeroCaptionButtons = false
            });
            // HwndSource 后登记的处理器先收到消息，最大化区要先于窗框处理命中。
            source?.AddHook(HandleNativeMessage);
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
            if (source is { IsDisposed: false }) source.RemoveHook(HandleNativeMessage);
            source = null;
        };
    }

    void BuildFrame()
    {
        frame.RowDefinitions.Add(new RowDefinition { Height = new GridLength(TitleHeight) });
        frame.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(minimizeButton);
        buttons.Children.Add(maximizeButton);
        buttons.Children.Add(closeButton);
        DockPanel.SetDock(buttons, Dock.Right);
        titleBar.Children.Add(buttons);
        var brand = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
        brand.Children.Add(new Image { Source = Icon, Width = 22, Height = 22, Margin = new Thickness(0, 0, 9, 0) });
        windowTitle.Text = Title;
        windowTitle.VerticalAlignment = VerticalAlignment.Center;
        windowTitle.FontSize = 13;
        brand.Children.Add(windowTitle);
        titleBar.Children.Add(brand);
        frame.Children.Add(titleBar);
        Grid.SetRow(page, 1);
        frame.Children.Add(page);
        Content = frame;
        ApplyFrameTheme("light");
    }

    Button CaptionButton(string glyph, string name, Action invoke)
    {
        var button = new Button
        {
            Content = glyph, ToolTip = name, Width = 46, Height = TitleHeight,
            FontSize = 18, Background = Brushes.Transparent, BorderThickness = new Thickness(0)
        };
        var container = new FrameworkElementFactory(typeof(Border));
        container.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        var label = new FrameworkElementFactory(typeof(ContentPresenter));
        label.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        label.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        container.AppendChild(label);
        button.Template = new ControlTemplate(typeof(Button)) { VisualTree = container };
        AutomationProperties.SetName(button, name);
        WindowChrome.SetIsHitTestVisibleInChrome(button, true);
        button.Click += (_, _) => invoke();
        button.MouseEnter += (_, _) => RefreshCaptionButton(button);
        button.MouseLeave += (_, _) => RefreshCaptionButton(button);
        button.GotKeyboardFocus += (_, _) => RefreshCaptionButton(button);
        button.LostKeyboardFocus += (_, _) => RefreshCaptionButton(button);
        return button;
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
        frame.Margin = maximized ? resizeBorder : new Thickness(0);
        maximizeButton.Content = maximized ? "❐" : "□";
        maximizeButton.ToolTip = maximized ? "还原窗口" : "最大化";
        AutomationProperties.SetName(maximizeButton, (string)maximizeButton.ToolTip);
        if (pageReady) Reply(new { kind = "window-state", state = WindowMode });
        UpdateReadoutSubscription();
    }

    IntPtr HandleNativeMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return IntPtr.Zero;
        if (message == NonClientHitTest && maximizeButton.IsVisible && maximizeButton.IsEnabled && PresentationSource.FromVisual(maximizeButton) is not null)
        {
            long point = lParam.ToInt64();
            var local = maximizeButton.PointFromScreen(new Point(unchecked((short)point), unchecked((short)(point >> 16))));
            if (new Rect(maximizeButton.RenderSize).Contains(local))
            {
                handled = true;
                return new IntPtr(MaxButtonHit);
            }
        }
        if (message == NonClientMouseLeave || (message == NonClientMouseMove && wParam.ToInt64() != MaxButtonHit))
        {
            maxPressed = false;
            SetMaxHover(false);
        }
        else if (wParam.ToInt64() == MaxButtonHit)
        {
            if (message == NonClientMouseMove && !maxHover)
            {
                SetMaxHover(true);
                var tracking = new MouseTracking { Size = (uint)Marshal.SizeOf<MouseTracking>(), Flags = 0x12, Window = hwnd };
                TrackMouseEvent(ref tracking);
            }
            if (message == NonClientLeftDown)
            {
                maxPressed = true;
                handled = true;
            }
            else if (message == NonClientLeftUp)
            {
                bool invoke = maxPressed;
                maxPressed = false;
                handled = true;
                if (invoke) Dispatcher.BeginInvoke(new Action(ToggleMaximize));
            }
        }
        return IntPtr.Zero;
    }

    void SetMaxHover(bool value)
    {
        maxHover = value;
        RefreshCaptionButton(maximizeButton);
    }

    void RefreshCaptionButton(Button button)
    {
        bool highlighted = button.IsMouseOver || button.IsKeyboardFocused || (button == maximizeButton && maxHover);
        button.Background = highlighted ? captionHover : Brushes.Transparent;
        button.Foreground = captionInk;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MouseTracking { public uint Size, Flags; public IntPtr Window; public uint HoverTime; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool TrackMouseEvent(ref MouseTracking tracking);

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
        captionHover = contrast ? SystemColors.ControlLightBrush : Palette(dark ? 0x294147 : 0xe2ebe2);
        Background = frame.Background = titleBar.Background = background;
        windowTitle.Foreground = captionInk;
        foreach (var button in new[] { minimizeButton, maximizeButton, closeButton }) RefreshCaptionButton(button);
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
                Reply(new { kind = "init", token = sessionToken, windowState = WindowMode });
                UpdateReadoutSubscription();
                return;
            }
            if (message.GetProperty("token").GetString() != sessionToken) return;
            if (kind == "theme") { ApplyFrameTheme(message.GetProperty("theme").GetString()); return; }
            if (kind != "query") return;
            id = message.GetProperty("id").GetInt32();
            var api = message.GetProperty("api").GetString()!;
            var metric = message.TryGetProperty("metric", out var metricValue) ? metricValue.GetString() : null;
            var hours = message.TryGetProperty("hours", out var hoursValue) ? hoursValue.GetInt32() : 24;
            object? result;
            if(api=="show-settings"){SettingsRequested?.Invoke();result=new{status="opened"};}
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

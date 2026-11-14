using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using ShapePath = System.Windows.Shapes.Path;

namespace Zhulong.Desktop;

internal sealed class ResourceDockWindow : Window
{
    const double RailWidth = 14, RailHeight = 88, PanelWidth = 72, PanelHeight = 244;
    const double TransitionSeconds = .215, PulseSeconds = 1.35, CircleRadius = 22, CircleCenter = 24;
    static readonly Color Jade = Color.FromRgb(87, 214, 162), Amber = Color.FromRgb(255, 186, 94),
        Red = Color.FromRgb(255, 107, 118), Gray = Color.FromRgb(157, 157, 154);
    static readonly Brush PanelBrush = FrozenBrush(49, 49, 49), BorderBrushDark = FrozenBrush(77, 77, 76),
        TrackBrush = FrozenBrush(101, 101, 100), TextBrush = FrozenBrush(241, 241, 237),
        MutedBrush = FrozenBrush(192, 192, 187), Paper = FrozenBrush(240, 237, 228),
        Ink = FrozenBrush(21, 32, 30), ProgressBrush = FrozenBrush(Red);
    static readonly Brush JadeMask = CreateJadeMask();

    readonly Border shell;
    readonly Grid rail;
    readonly StackPanel details;
    readonly Rectangle dragon, progress, progressTrack;
    readonly TextBlock memory, cpu, commit, download, upload;
    readonly Ellipse diskRim, ripple, darkEye, lightEye;
    readonly ShapePath yin;
    readonly RotateTransform diskRotation = new();
    readonly ScaleTransform rippleScale = new(1, 1);
    readonly RectAnimation move = new();

    SystemSnapshot? sample;
    double? memoryPercent, cpuPercent;
    Rect workArea = SystemParameters.WorkArea;
    double angle, pulseElapsed = PulseSeconds;
    long previousFrame;
    bool opened, rendering;

    internal ResourceDockWindow()
    {
        Title = "烛龙 · 资源挂件";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        UseLayoutRounding = true;

        var scene = new Grid { ClipToBounds = true };
        shell = new Border { Background = PanelBrush, BorderBrush = BorderBrushDark, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7) };
        scene.Children.Add(shell);

        rail = new Grid { Width = RailWidth, Height = RailHeight, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        dragon = new Rectangle
        {
            Width = 10, Height = 12, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 5, 0, 0), Fill = FrozenBrush(Gray),
            OpacityMask = JadeMask
        };
        rail.Children.Add(dragon);
        var track = new Grid
        {
            Width = 6, Height = 59, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 23, 0, 0),
            Clip = new RectangleGeometry(new Rect(0, 0, 6, 59), 3, 3)
        };
        progressTrack = new Rectangle { Fill = TrackBrush, RadiusX = 3, RadiusY = 3 };
        progress = new Rectangle { Fill = ProgressBrush, RadiusX = 3, RadiusY = 3, Height = 0, VerticalAlignment = VerticalAlignment.Bottom };
        track.Children.Add(progressTrack);
        track.Children.Add(progress);
        rail.Children.Add(track);
        scene.Children.Add(rail);

        details = new StackPanel
        {
            Width = PanelWidth, Height = PanelHeight, Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center, Opacity = 0, Visibility = Visibility.Collapsed
        };
        var meter = new Grid { Width = 48, Height = 48 };
        diskRim = new Ellipse { Margin = new Thickness(0), Stroke = dragon.Fill, StrokeThickness = 1 };
        meter.Children.Add(diskRim);

        var disk = new Canvas { Width = 48, Height = 48, RenderTransformOrigin = new Point(.5, .5), RenderTransform = diskRotation };
        var baseDisk = new Ellipse { Fill = Paper, Width = 44, Height = 44 };
        Canvas.SetLeft(baseDisk, 2);
        Canvas.SetTop(baseDisk, 2);
        disk.Children.Add(baseDisk);
        yin = new ShapePath { Fill = Ink };
        disk.Children.Add(yin);
        darkEye = new Ellipse { Fill = Ink };
        lightEye = new Ellipse { Fill = Paper };
        disk.Children.Add(darkEye);
        disk.Children.Add(lightEye);
        meter.Children.Add(disk);

        ripple = new Ellipse
        {
            Stroke = dragon.Fill, StrokeThickness = 1.1, Opacity = 0, RenderTransformOrigin = new Point(.5, .5),
            RenderTransform = rippleScale, IsHitTestVisible = false
        };
        var meterLayer = new Grid { Height = 66 };
        var meterButton = new Button
        {
            Width = 54, Height = 58, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 9, 0, 0), Padding = new Thickness(0),
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Content = meter,
            ToolTip = "点击太极，短暂加速并扩散"
        };
        var buttonTemplate = new ControlTemplate(typeof(Button));
        buttonTemplate.VisualTree = new FrameworkElementFactory(typeof(ContentPresenter));
        meterButton.Template = buttonTemplate;
        System.Windows.Automation.AutomationProperties.SetName(meterButton, "太极动效");
        meterButton.Click += (_, _) => StartPulse();
        ripple.Width = 48;
        ripple.Height = 48;
        ripple.Margin = new Thickness(0, 14, 0, 0);
        ripple.VerticalAlignment = VerticalAlignment.Top;
        meterLayer.Children.Add(ripple);
        meterLayer.Children.Add(meterButton);
        details.Children.Add(meterLayer);

        memory = Text("—", 19, TextBrush, true);
        memory.HorizontalAlignment = HorizontalAlignment.Center;
        details.Children.Add(memory);
        var memoryLabel = Text("物理内存", 10, MutedBrush);
        memoryLabel.HorizontalAlignment = HorizontalAlignment.Center;
        details.Children.Add(memoryLabel);
        details.Children.Add(new Border { Height = 1, Background = BorderBrushDark, Margin = new Thickness(7, 9, 7, 6) });
        cpu = AddMetric("CPU");
        commit = AddMetric("提交");
        download = AddMetric("↓");
        upload = AddMetric("↑");
        scene.Children.Add(details);
        Content = scene;
        SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(NoMouseActivation);
        MouseEnter += (_, _) => SetExpanded(true);
        MouseLeave += (_, _) => SetExpanded(false);
        KeyDown += (_, e) => { if (e.Key == Key.Escape) SetExpanded(false); };
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) SetExpanded(false);
        };
        Closed += (_, _) => StopRendering();
        UpdateGeometry(.0);
        UpdateVisual(0);
        UpdateDisk();
    }

    internal void ApplyLive(LiveSystemReadout? value)
    {
        var next = Normalize(value?.MemoryPercent);
        cpuPercent = Normalize(value?.CpuPercent);
        if (memoryPercent != next)
        {
            memoryPercent = next;
            var brush = FrozenBrush(LoadColor(next));
            dragon.Fill = diskRim.Stroke = ripple.Stroke = brush;
            progress.Height = next * .59 ?? 0;
            if (opened) UpdateDisk();
            UpdateRendering();
        }
        if (opened) UpdateReadings();
    }

    internal void ApplySample(SystemSnapshot? value)
    {
        sample = value;
        if (opened) UpdateReadings();
    }

    void SetExpanded(bool expand)
    {
        if (opened == expand && IsVisible) return;
        opened = expand;
        workArea = SystemParameters.WorkArea;
        if (expand)
        {
            details.Visibility = Visibility.Visible;
            UpdateReadings();
            UpdateDisk();
        }
        else
        {
            pulseElapsed = PulseSeconds;
            ripple.Opacity = 0;
        }
        if (!IsVisible || !SystemParameters.ClientAreaAnimation)
        {
            move.Finish(expand);
            UpdateGeometry(move.Progress);
            UpdateVisual(move.Progress);
            if (!expand) details.Visibility = Visibility.Collapsed;
        }
        else move.Start(expand);
        UpdateRendering();
    }

    void StartPulse()
    {
        if (!opened || !IsVisible || !SystemParameters.ClientAreaAnimation) return;
        pulseElapsed = 0;
        UpdateRendering();
    }

    void UpdateRendering()
    {
        var animate = IsVisible && SystemParameters.ClientAreaAnimation &&
            (move.Pending || (opened && (memoryPercent.HasValue || pulseElapsed < PulseSeconds)));
        if (animate == rendering) return;
        if (!animate)
        {
            StopRendering();
            return;
        }
        rendering = true;
        previousFrame = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += OnFrame;
    }

    void StopRendering()
    {
        if (!rendering) return;
        CompositionTarget.Rendering -= OnFrame;
        rendering = false;
    }

    void OnFrame(object? sender, EventArgs args)
    {
        if (!IsVisible || !SystemParameters.ClientAreaAnimation)
        {
            move.Finish(opened);
            UpdateGeometry(move.Progress);
            UpdateVisual(move.Progress);
            pulseElapsed = PulseSeconds;
            ripple.Opacity = 0;
            if (!opened) details.Visibility = Visibility.Collapsed;
            StopRendering();
            return;
        }

        long now = Stopwatch.GetTimestamp();
        double dt = Stopwatch.GetElapsedTime(previousFrame, now).TotalSeconds;
        if (dt < 1d / 34) return;
        previousFrame = now;

        if (move.Pending)
        {
            move.Advance(dt);
            UpdateGeometry(move.Progress);
            UpdateVisual(move.Progress);
            if (!move.Pending && !opened) details.Visibility = Visibility.Collapsed;
        }
        if (opened)
        {
            if (memoryPercent is { } percent)
            {
                var pulseFactor = PulseMultiplier((pulseElapsed + Math.Min(PulseSeconds, pulseElapsed + dt)) * .5);
                angle = (angle + dt * 360 / RotationPeriod(percent) * pulseFactor) % 360;
                diskRotation.Angle = angle;
            }
            if (pulseElapsed < PulseSeconds)
            {
                pulseElapsed = Math.Min(PulseSeconds, pulseElapsed + dt);
                var p = pulseElapsed / PulseSeconds;
                var spread = 1 - Math.Pow(1 - p, 3);
                rippleScale.ScaleX = rippleScale.ScaleY = 1 + spread * .40;
                ripple.Opacity = (1 - p) * .85;
            }
        }
        UpdateRendering();
    }

    void UpdateGeometry(double progress)
    {
        var width = RailWidth + (PanelWidth - RailWidth) * progress;
        var height = RailHeight + (PanelHeight - RailHeight) * progress;
        Width = width;
        Height = height;
        Left = workArea.Right - width;
        Top = workArea.Top + (workArea.Height - height) / 2;
    }

    void UpdateVisual(double value)
    {
        rail.Opacity = 1 - Math.Min(1, value * 2.4);
        details.Opacity = Math.Clamp((value - .25) * 1.45, 0, 1);
        details.IsHitTestVisible = value > .50 && opened;
        shell.CornerRadius = new CornerRadius(7 + value * 7);
    }

    void UpdateDisk()
    {
        var percent = memoryPercent ?? 50;
        yin.Data = YinGeometry(percent);
        var lower = CircleRadius * percent / 100;
        var upper = CircleRadius - lower;
        var eye = Math.Min(lower, upper) * .23;
        PositionEye(darkEye, CircleCenter - CircleRadius + upper, eye);
        PositionEye(lightEye, CircleCenter + CircleRadius - lower, eye);
    }

    static void PositionEye(Ellipse eye, double y, double radius)
    {
        eye.Width = eye.Height = radius * 2;
        Canvas.SetLeft(eye, CircleCenter - radius);
        Canvas.SetTop(eye, y - radius);
    }

    void UpdateReadings()
    {
        memory.Text = Percent(memoryPercent);
        cpu.Text = Percent(cpuPercent);
        commit.Text = Percent(sample?.CommitPercent);
        download.Text = Network("net_down_mb_s");
        upload.Text = Network("net_up_mb_s");
    }

    string Network(string key)
    {
        if (sample?.Metrics is not { } metrics || !metrics.TryGetValue(key, out var metric) || metric.State != "ok" ||
            metric.Value is not { } value || !double.IsFinite(value) || value < 0) return "—";
        if (value >= 1024) return $"{value / 1024:0.#} GB/s";
        return value >= 1 ? $"{value:0.#} MB/s" : $"{value * 1024:0} KB/s";
    }

    TextBlock AddMetric(string label)
    {
        var row = new DockPanel { Margin = new Thickness(7, 2, 7, 2), Height = 17 };
        var left = Text(label, 10, MutedBrush);
        DockPanel.SetDock(left, Dock.Left);
        row.Children.Add(left);
        var right = Text("—", 10, TextBrush, true);
        right.TextAlignment = TextAlignment.Right;
        row.Children.Add(right);
        details.Children.Add(row);
        return right;
    }

    static TextBlock Text(string value, double size, Brush color, bool strong = false) => new()
    {
        Text = value, FontSize = size, Foreground = color, FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal,
        VerticalAlignment = VerticalAlignment.Center
    };

    static IntPtr NoMouseActivation(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int mouseActivate = 0x21;
        if (message != mouseActivate) return IntPtr.Zero;
        handled = true;
        return new IntPtr(3);
    }

    static Brush CreateJadeMask()
    {
        using var stream = Application.GetResourceStream(new Uri("Assets/zhulong-jade-mask.png", UriKind.Relative)).Stream;
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        var mask = new ImageBrush(bitmap) { Stretch = Stretch.Uniform };
        mask.Freeze();
        return mask;
    }

    static SolidColorBrush FrozenBrush(byte r, byte g, byte b) => FrozenBrush(Color.FromRgb(r, g, b));
    static SolidColorBrush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    static double? Normalize(double? value) => value is { } n && double.IsFinite(n) ? Math.Clamp(n, 0, 100) : null;
    static string Percent(double? value) => Normalize(value) is { } n ? $"{n:F0}%" : "—";
    internal static Color LoadColor(double? value) => Normalize(value) switch { null => Gray, < 70 => Jade, < 85 => Amber, _ => Red };
    internal static double RotationPeriod(double value) => 60 / (1 + .09 * (Normalize(value) ?? 0));
    static double PulseMultiplier(double elapsed) => 1 + 2 * Math.Pow(Math.Clamp(1 - elapsed / PulseSeconds, 0, 1), 2);

    internal static Geometry YinGeometry(double value)
    {
        var percent = Normalize(value) ?? 50;
        if (percent == 0) return Geometry.Empty;
        if (percent == 100)
        {
            var ellipse = new EllipseGeometry(new Point(CircleCenter, CircleCenter), CircleRadius, CircleRadius);
            ellipse.Freeze();
            return ellipse;
        }
        var lower = CircleRadius * percent / 100;
        var upper = CircleRadius - lower;
        // 两段分界弧的面积差与占用比例线性对应，等大的鱼眼相互抵消。
        var top = new Point(CircleCenter, CircleCenter - CircleRadius);
        var bottom = new Point(CircleCenter, CircleCenter + CircleRadius);
        var split = new Point(CircleCenter, bottom.Y - lower * 2);
        var shape = new StreamGeometry();
        using (var path = shape.Open())
        {
            path.BeginFigure(top, true, true);
            path.ArcTo(bottom, new Size(CircleRadius, CircleRadius), 0, false, SweepDirection.Clockwise, true, false);
            path.ArcTo(split, new Size(lower, lower), 0, false, SweepDirection.Clockwise, true, false);
            path.ArcTo(top, new Size(upper, upper), 0, false, SweepDirection.Counterclockwise, true, false);
        }
        shape.Freeze();
        return shape;
    }

    internal static void CheckBehavior(Action<bool, string> check)
    {
        foreach (var value in new[] { 0d, 25, 50, 75, 100 })
        {
            var areaFraction = YinGeometry(value).GetArea(.002, ToleranceType.Absolute) / (Math.PI * CircleRadius * CircleRadius);
            check(Math.Abs(areaFraction - value / 100) < .002, "太极阴鱼真实面积须匹配物理内存比例");
        }
        check(LoadColor(null) == Gray && LoadColor(69) == Jade && LoadColor(70) == Amber &&
            LoadColor(84) == Amber && LoadColor(85) == Red, "龙纹颜色须覆盖未知及两道占用边界");
        check(RotationPeriod(0) == 60 && RotationPeriod(100) == 6 && RotationPeriod(75) < RotationPeriod(50), "太极转速须随占用变化");
        check(PulseMultiplier(0) == 3 && PulseMultiplier(PulseSeconds) == 1 && PulseMultiplier(.7) < PulseMultiplier(.2),
            "太极点击后从三倍加速回落至基础转速");
        var transition = new RectAnimation();
        transition.Start(true);
        transition.Advance(.08);
        check(transition.Progress is > 0 and < 1 && transition.Pending, "悬停展开须有中间帧");
        transition.Start(false);
        transition.Advance(TransitionSeconds);
        check(transition.Progress == 0 && !transition.Pending, "展开途中移开须完整收起并结束过渡");
        var dock = new ResourceDockWindow { IsHitTestVisible = false };
        try
        {
            dock.ApplyLive(new(DateTimeOffset.UtcNow, 20, 85, 8, 10));
            dock.Show();
            dock.SetExpanded(true);
            PumpFrames(380);
            check(dock.Width == PanelWidth && dock.memory.Text == "85%" && dock.cpu.Text == "20%" &&
                dock.details.Visibility == Visibility.Visible && dock.rail.Opacity == 0, "实际窗口展开须替换细条并更新读数");
            if (SystemParameters.ClientAreaAnimation)
            {
                check(dock.rendering && dock.diskRotation.Angle > 0, "展开后原生渲染应使太极旋转");
                dock.StartPulse();
                PumpFrames(160);
                check(dock.ripple.Opacity > 0 && dock.rippleScale.ScaleX > 1, "点击动效应产生扩散");
            }
            dock.SetExpanded(false);
            var angle = dock.diskRotation.Angle;
            PumpFrames(380);
            check(dock.Width == RailWidth && dock.Height == RailHeight && !dock.rendering && dock.diskRotation.Angle == angle &&
                dock.details.Visibility == Visibility.Collapsed && dock.ripple.Opacity == 0, "收起须停转、取消扩散和解绑渲染");
        }
        finally { dock.Close(); }
    }

    static void PumpFrames(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    sealed class RectAnimation
    {
        double start, end, elapsed;
        internal double Progress { get; private set; }
        internal bool Pending => Progress != end;
        internal void Start(bool open)
        {
            start = Progress;
            end = open ? 1 : 0;
            elapsed = 0;
        }
        internal void Advance(double dt)
        {
            elapsed = Math.Min(TransitionSeconds, elapsed + dt);
            var ratio = elapsed / TransitionSeconds;
            Progress = ratio >= 1 ? end : start + (end - start) * (1 - Math.Pow(1 - ratio, 3));
        }
        internal void Finish(bool open) => Progress = end = open ? 1 : 0;
    }
}

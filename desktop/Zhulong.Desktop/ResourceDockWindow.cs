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
    const double BurstMargin = 36, TransitionSeconds = .22, SpinSeconds = 1, RippleSeconds = 1, OuterRippleSeconds = 1,
        ResultSeconds = 1, AccelerationSeconds = SpinSeconds + RippleSeconds + OuterRippleSeconds,
        CircleRadius = 22, CircleCenter = 24, RippleMaxScale = 6,
        OuterRippleMargin = CircleRadius * RippleMaxScale - PanelWidth / 2 + 4;
    static readonly Color Jade = Color.FromRgb(87, 214, 162), Amber = Color.FromRgb(255, 186, 94),
        Red = Color.FromRgb(255, 107, 118), Gray = Color.FromRgb(157, 157, 154);
    static readonly Brush PanelBrush = FrozenBrush(49, 49, 49), BorderBrushDark = FrozenBrush(77, 77, 76),
        TrackBrush = FrozenBrush(101, 101, 100), TextBrush = FrozenBrush(241, 241, 237),
        MutedBrush = FrozenBrush(192, 192, 187), Paper = FrozenBrush(240, 237, 228),
        Ink = FrozenBrush(21, 32, 30), ProgressBrush = FrozenBrush(Red);
    static readonly Brush JadeMask = CreateJadeMask();

    readonly Canvas scene, contents;
    readonly RectangleGeometry panelClip = new();
    readonly Grid rail;
    readonly StackPanel details;
    readonly Rectangle dragon, progress, progressTrack;
    readonly TextBlock memory, cpu, cpuTemperature, commit, download, upload, accelerationStatus;
    readonly Ellipse diskRim;
    readonly Image disk, ripple;
    readonly DockBagua bagua = new(FrozenBrush(Jade));
    readonly Grid diskSurface;
    readonly Button meterButton;
    readonly ZhulongContextMenu dockMenu;
    readonly RotateTransform diskRotation = new();
    readonly ScaleTransform rippleScale = new(1, 1);
    readonly TranslateTransform railOffset = new();
    readonly RectAnimation move = new();
    readonly DispatcherTimer resultTimer = new() { Interval = TimeSpan.FromSeconds(ResultSeconds) };

    SystemSnapshot? sample;
    double? memoryPercent, cpuPercent;
    Rect workArea = SystemParameters.WorkArea;
    double anchorY, panelTop, panelLeft;
    double angle;
    long previousFrame, accelerationStarted;
    TimeSpan previousRenderingTime;
    AccelerationPhase accelerationPhase;
    string? accelerationResult;
    Point? dragStart;
    Point lastDragPoint;
    double dragAnchorY, dragLeft;
    bool opened, rendering, expandedBounds, outerRippleBounds, dockRight = true, dragging;

    internal event Action? AccelerateRequested;
    internal event Action? OpenRequested;
    internal event Action? HideRequested;
    internal event Action<bool>? ExpandedChanged;

    enum AccelerationPhase { Idle, Animating, Waiting, Result }

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

        scene = new Canvas { ClipToBounds = true };
        var shell = new ShapePath { Data = panelClip, Fill = PanelBrush, Stroke = BorderBrushDark, StrokeThickness = 1 };
        scene.Children.Add(shell);
        contents = new Canvas { Clip = panelClip };
        scene.Children.Add(contents);

        rail = new Grid { Width = RailWidth, Height = RailHeight, RenderTransform = railOffset };
        dragon = new Rectangle
        {
            Width = 12, Height = 15, HorizontalAlignment = HorizontalAlignment.Center,
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
        contents.Children.Add(rail);

        details = new StackPanel
        {
            Width = PanelWidth, Height = PanelHeight, Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center, Opacity = 0, Visibility = Visibility.Collapsed
        };
        diskSurface = new Grid { Width = 48, Height = 48 };
        diskRim = new Ellipse { Margin = new Thickness(0), Stroke = dragon.Fill, StrokeThickness = 1 };
        diskSurface.Children.Add(diskRim);

        disk = new Image { Width = 48, Height = 48, RenderTransformOrigin = new Point(.5, .5), RenderTransform = diskRotation };
        diskSurface.Children.Add(disk);

        var rippleTransform = new TransformGroup();
        rippleTransform.Children.Add(diskRotation);
        rippleTransform.Children.Add(rippleScale);
        ripple = new Image
        {
            Width = 48, Height = 48, Opacity = 0, RenderTransformOrigin = new Point(.5, .5),
            RenderTransform = rippleTransform, IsHitTestVisible = false
        };
        scene.Children.Add(ripple);
        // 扩散图盖在面板上方，透明区域保持可穿透。
        System.Windows.Controls.Panel.SetZIndex(ripple, 1);
        scene.Children.Add(bagua);
        System.Windows.Controls.Panel.SetZIndex(bagua, 1);
        var meterLayer = new Grid { Height = 66 };
        var meter = new Grid { Width = PanelWidth - 6, Height = 48 };
        meter.Children.Add(diskSurface);
        accelerationStatus = Text("", 11, TextBrush, true);
        accelerationStatus.TextAlignment = TextAlignment.Center;
        accelerationStatus.TextWrapping = TextWrapping.Wrap;
        accelerationStatus.TextTrimming = TextTrimming.CharacterEllipsis;
        accelerationStatus.MaxHeight = 40;
        accelerationStatus.Visibility = Visibility.Collapsed;
        meter.Children.Add(accelerationStatus);
        meterButton = new Button
        {
            Width = meter.Width, Height = 48, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 14, 0, 0), Padding = new Thickness(0),
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Content = meter,
            ToolTip = "点击太极整理白名单进程内存"
        };
        var buttonTemplate = new ControlTemplate(typeof(Button));
        buttonTemplate.VisualTree = new FrameworkElementFactory(typeof(ContentPresenter));
        meterButton.Template = buttonTemplate;
        System.Windows.Automation.AutomationProperties.SetName(meterButton, "内存加速");
        meterButton.Click += (_, _) => ActivateAcceleration();
        meterButton.MouseEnter += (_, _) => UpdateRendering();
        meterButton.MouseLeave += (_, _) => UpdateRendering();
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
        cpuTemperature = AddMetric("温度");
        cpuTemperature.ToolTip = "处理器温度";
        commit = AddMetric("提交");
        download = AddMetric("↓");
        upload = AddMetric("↑");
        contents.Children.Add(details);
        Content = scene;
        resultTimer.Tick += (_, _) => ClearAcceleration();
        dockMenu = new ZhulongContextMenu();
        dockMenu.Add("打开烛龙", () => OpenRequested?.Invoke());
        dockMenu.Add("整理内存", AccelerateFromMenu);
        dockMenu.Items.Add(new Separator());
        dockMenu.Add("仅在托盘显示", () => HideRequested?.Invoke());
        dockMenu.Closed += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (IsVisible && !dockMenu.IsOpen && accelerationPhase == AccelerationPhase.Idle) SetExpanded(PointerOnDock());
        }));
        SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(NoMouseActivation);
        MouseEnter += (_, _) => { if (dragStart is null && !dockMenu.IsOpen) SetExpanded(true); };
        MouseLeave += (_, _) =>
        {
            if (dragStart is null && !dockMenu.IsOpen && accelerationPhase == AccelerationPhase.Idle) SetExpanded(false);
        };
        PreviewMouseRightButtonUp += (_, e) =>
        {
            if (dragStart is not null || !panelClip.FillContains(e.GetPosition(scene))) return;
            dockMenu.OpenAtPointer(this);
            e.Handled = true;
        };
        PreviewMouseLeftButtonDown += BeginDrag;
        PreviewMouseMove += UpdateDrag;
        PreviewMouseLeftButtonUp += EndDrag;
        LostMouseCapture += (_, _) => FinishDrag();
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            FinishDrag();
            SetExpanded(false);
        };
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible)
            {
                ClearAcceleration();
                SetExpanded(false);
            }
        };
        Closed += (_, _) =>
        {
            dockMenu.IsOpen = false;
            resultTimer.Stop();
            bagua.SetActive(false);
            StopRendering();
        };
        anchorY = workArea.Top + workArea.Height / 2;
        SetWindowBounds(false);
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
            dragon.Fill = diskRim.Stroke = brush;
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

    internal void ApplyNetwork(LiveNetworkReadout? value)
    {
        download.Text = Network(value?.DownloadBytesPerSecond);
        upload.Text = Network(value?.UploadBytesPerSecond);
    }

    internal void CompleteAcceleration(string text)
    {
        if (accelerationPhase is not (AccelerationPhase.Animating or AccelerationPhase.Waiting)) return;
        accelerationResult = text;
        if (accelerationPhase == AccelerationPhase.Waiting) ShowAccelerationFeedback();
    }

    void ShowAccelerationFeedback()
    {
        var text = accelerationResult ?? "整理中…";
        accelerationStatus.Text = text;
        accelerationStatus.ToolTip = text;
        accelerationStatus.Visibility = Visibility.Visible;
        diskSurface.Visibility = Visibility.Hidden;
        accelerationPhase = accelerationResult is null ? AccelerationPhase.Waiting : AccelerationPhase.Result;
        if (dragStart is null) SetOuterRippleBounds(false);
        if (accelerationPhase == AccelerationPhase.Result) resultTimer.Start();
        UpdateRendering();
    }

    void ClearAcceleration()
    {
        resultTimer.Stop();
        accelerationPhase = AccelerationPhase.Idle;
        accelerationResult = null;
        accelerationStatus.Text = "";
        accelerationStatus.ToolTip = null;
        accelerationStatus.Visibility = Visibility.Collapsed;
        ripple.Opacity = 0;
        diskSurface.Visibility = Visibility.Visible;
        if (dragStart is null) SetOuterRippleBounds(false);
        if (IsVisible && dragStart is null && !dockMenu.IsOpen && !PointerOnDock()) SetExpanded(false);
        UpdateRendering();
    }

    void BeginDrag(object sender, MouseButtonEventArgs e)
    {
        if (meterButton.IsMouseOver || !panelClip.FillContains(e.GetPosition(scene))) return;
        workArea = SystemParameters.WorkArea;
        dragStart = lastDragPoint = DesktopPoint(e);
        dragAnchorY = anchorY;
        dragLeft = Left;
        if (!CaptureMouse()) dragStart = null;
        e.Handled = true;
    }

    void UpdateDrag(object sender, MouseEventArgs e)
    {
        if (dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
        Point point = lastDragPoint = DesktopPoint(e);
        if (!dragging)
        {
            if (Math.Abs(point.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(point.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            dragging = true;
        }
        anchorY = dragAnchorY + point.Y - start.Y;
        SetWindowBounds(expandedBounds, dragLeft + point.X - start.X);
    }

    void EndDrag(object sender, MouseButtonEventArgs e)
    {
        if (dragStart is null) return;
        e.Handled = true;
        FinishDrag();
    }

    void FinishDrag()
    {
        if (dragStart is null) return;
        if (dragging) SnapDock(lastDragPoint.X);
        var pointerOnPanel = panelClip.Rect.Contains(new Point(lastDragPoint.X - Left, lastDragPoint.Y - Top));
        dragStart = null;
        dragging = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (accelerationPhase != AccelerationPhase.Animating) SetOuterRippleBounds(false);
        SetExpanded(pointerOnPanel);
    }

    Point DesktopPoint(MouseEventArgs e)
    {
        Point pixels = PointToScreen(e.GetPosition(this));
        return PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice.Transform(pixels) ?? pixels;
    }

    void SnapDock(double x)
    {
        dockRight = x >= workArea.Left + workArea.Width / 2;
        anchorY = Math.Clamp(anchorY, workArea.Top + RailHeight / 2, Math.Max(workArea.Top + RailHeight / 2, workArea.Bottom - RailHeight / 2));
        SetWindowBounds(expandedBounds);
    }

    void SetExpanded(bool expand)
    {
        if (!expand && IsVisible && accelerationPhase != AccelerationPhase.Idle) return;
        if (opened == expand && IsVisible) return;
        var changed = opened != expand;
        opened = expand;
        if (changed) ExpandedChanged?.Invoke(expand);
        workArea = SystemParameters.WorkArea;
        if (expand)
        {
            details.Visibility = Visibility.Visible;
            UpdateReadings();
            UpdateDisk();
            SetWindowBounds(true);
        }
        if (!IsVisible || !SystemParameters.ClientAreaAnimation)
        {
            move.Finish(expand);
            SetWindowBounds(expand);
            UpdateVisual(move.Progress);
            if (!expand) details.Visibility = Visibility.Collapsed;
        }
        else move.Start(expand);
        UpdateRendering();
    }

    void ActivateAcceleration()
    {
        if (!IsVisible || accelerationPhase != AccelerationPhase.Idle || AccelerateRequested is null) return;
        SetExpanded(true);
        accelerationPhase = AccelerationPhase.Animating;
        accelerationStarted = Stopwatch.GetTimestamp();
        if (!SystemParameters.ClientAreaAnimation) ShowAccelerationFeedback();
        UpdateRendering();
        AccelerateRequested?.Invoke();
    }

    void AccelerateFromMenu()
    {
        dockMenu.IsOpen = false;
        ActivateAcceleration();
    }

    bool PointerOnDock()
    {
        if (PresentationSource.FromVisual(scene) is null) return false;
        var cursor = System.Windows.Forms.Cursor.Position;
        return panelClip.FillContains(scene.PointFromScreen(new Point(cursor.X, cursor.Y)));
    }

    void UpdateRendering()
    {
        var motion = IsVisible && SystemParameters.ClientAreaAnimation;
        bagua.SetActive(motion && opened && meterButton.IsMouseOver &&
            accelerationPhase == AccelerationPhase.Idle && !dockMenu.IsOpen);
        var animate = motion &&
            (move.Pending || accelerationPhase == AccelerationPhase.Animating ||
                bagua.Active || (opened && memoryPercent.HasValue && accelerationPhase == AccelerationPhase.Idle));
        if (animate == rendering) return;
        if (!animate)
        {
            StopRendering();
            return;
        }
        rendering = true;
        previousFrame = Stopwatch.GetTimestamp();
        previousRenderingTime = TimeSpan.MinValue;
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
        if (args is RenderingEventArgs frame)
        {
            if (frame.RenderingTime == previousRenderingTime) return;
            previousRenderingTime = frame.RenderingTime;
        }
        if (!IsVisible || !SystemParameters.ClientAreaAnimation)
        {
            bagua.SetActive(false);
            move.Finish(opened);
            SetWindowBounds(opened);
            UpdateVisual(move.Progress);
            ripple.Opacity = 0;
            if (accelerationPhase == AccelerationPhase.Animating) ShowAccelerationFeedback();
            if (!opened) details.Visibility = Visibility.Collapsed;
            StopRendering();
            return;
        }

        long now = Stopwatch.GetTimestamp();
        double dt = Stopwatch.GetElapsedTime(previousFrame, now).TotalSeconds;
        var accelerating = accelerationPhase == AccelerationPhase.Animating;
        if (!move.Pending && !accelerating && dt < 1d / 34) return;
        var priorFrame = previousFrame;
        previousFrame = now;

        if (move.Pending)
        {
            move.Advance(dt);
            UpdateVisual(move.Progress);
            if (!move.Pending && !opened)
            {
                details.Visibility = Visibility.Collapsed;
                SetWindowBounds(false);
            }
        }
        if (opened && (accelerating || (accelerationPhase == AccelerationPhase.Idle && memoryPercent.HasValue)))
        {
            var spinTime = accelerating ? FastSpinDuration(
                Stopwatch.GetElapsedTime(accelerationStarted, priorFrame).TotalSeconds,
                Stopwatch.GetElapsedTime(accelerationStarted, now).TotalSeconds) : 0;
            var rotationTime = spinTime * 10 + (dt - spinTime) * (meterButton.IsMouseOver ? 3 : 1);
            angle = (angle + rotationTime * 360 / RotationPeriod(memoryPercent ?? 0)) % 360;
            diskRotation.Angle = angle;
        }
        if (accelerating) UpdateAccelerationAnimation(Stopwatch.GetElapsedTime(accelerationStarted, now).TotalSeconds);
        UpdateRendering();
        bagua.Advance(dt);
    }

    void UpdateAccelerationAnimation(double elapsed)
    {
        if (elapsed < SpinSeconds) return;
        double scale, opacity;
        if (elapsed < SpinSeconds + RippleSeconds)
        {
            var progress = (elapsed - SpinSeconds) / RippleSeconds;
            var spread = .75 * progress + .25 * progress * progress * (3 - 2 * progress);
            scale = 1 + spread * 2;
            opacity = .48 - .24 * progress;
        }
        else
        {
            if (dragStart is null) SetOuterRippleBounds(true);
            var progress = Math.Min(1, (elapsed - SpinSeconds - RippleSeconds) / OuterRippleSeconds);
            // 上一段末端每秒增加 1.5 倍，第三秒以两倍末速从 3 连续扩到 6。
            scale = 3 + (RippleMaxScale - 3) * progress;
            opacity = .24 * (1 - progress);
        }
        rippleScale.ScaleX = rippleScale.ScaleY = scale;
        ripple.Opacity = opacity;
        if (elapsed >= AccelerationSeconds) ShowAccelerationFeedback();
    }

    static double FastSpinDuration(double from, double until) =>
        Math.Clamp(until, 0, AccelerationSeconds) - Math.Clamp(from, 0, AccelerationSeconds);

    void SetOuterRippleBounds(bool expanded)
    {
        if (outerRippleBounds == expanded) return;
        outerRippleBounds = expanded;
        if (!expandedBounds) return;
        SetWindowBounds(true);
        // 原生窗口先移动，当前渲染帧也需应用内部坐标，避免扩散中心偏移一帧。
        UpdateLayout();
    }

    void SetWindowBounds(bool expand) => SetWindowBounds(expand, null);

    void SetWindowBounds(bool expand, double? dragX)
    {
        expandedBounds = expand;
        var height = expand ? PanelHeight : RailHeight;
        var visualTop = Math.Clamp(anchorY - height / 2, workArea.Top,
            Math.Max(workArea.Top, workArea.Bottom - height));
        var margin = expand ? (outerRippleBounds ? OuterRippleMargin : BurstMargin) : 0;
        var width = expand ? PanelWidth + margin : RailWidth;
        Width = width;
        Height = height + margin;
        contents.Width = Width;
        contents.Height = Height;
        var rightmost = Math.Max(workArea.Left, workArea.Right - width);
        Left = dragX is { } x ? Math.Clamp(x, workArea.Left, rightmost) : dockRight ? rightmost : workArea.Left;
        Top = visualTop - margin;
        panelLeft = dockRight ? margin : 0;
        panelTop = margin;
        Canvas.SetLeft(details, panelLeft);
        Canvas.SetTop(details, panelTop);
        Canvas.SetLeft(ripple, panelLeft + (PanelWidth - 48) / 2);
        Canvas.SetTop(ripple, panelTop + 14);
        bagua.Place(new Point(panelLeft + PanelWidth / 2, panelTop + 14 + CircleCenter), new Size(Width, Height));
        UpdateVisual(move.Progress);
    }

    void UpdateVisual(double value)
    {
        var width = RailWidth + (PanelWidth - RailWidth) * value;
        var height = RailHeight + (PanelHeight - RailHeight) * value;
        var left = expandedBounds && dockRight ? panelLeft + PanelWidth - width : panelLeft;
        var top = expandedBounds ? Math.Clamp(anchorY - height / 2 - Top, panelTop, panelTop + PanelHeight - height) : 0;
        panelClip.Rect = new Rect(left, top, width, height);
        panelClip.RadiusX = panelClip.RadiusY = 7 + value * 7;
        railOffset.X = dockRight ? left + width - RailWidth : left;
        railOffset.Y = top + (height - RailHeight) / 2;
        rail.Opacity = 1 - Math.Min(1, value * 2.4);
        rail.IsHitTestVisible = rail.Opacity > 0;
        details.Opacity = Math.Clamp((value - .25) * 1.45, 0, 1);
        details.IsHitTestVisible = value > .50 && opened;
    }

    void UpdateDisk()
    {
        var percent = memoryPercent ?? 50;
        var lower = CircleRadius * percent / 100;
        var upper = CircleRadius - lower;
        var eye = Math.Min(lower, upper) * .23;
        var drawing = new DrawingGroup();
        using (DrawingContext context = drawing.Open())
        {
            context.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, 48, 48));
            context.DrawEllipse(Paper, null, new Point(CircleCenter, CircleCenter), CircleRadius, CircleRadius);
            context.DrawGeometry(Ink, null, YinGeometry(percent));
            context.DrawEllipse(Ink, null, new Point(CircleCenter, CircleCenter - CircleRadius + upper), eye, eye);
            context.DrawEllipse(Paper, null, new Point(CircleCenter, CircleCenter + CircleRadius - lower), eye, eye);
        }
        var image = new DrawingImage(drawing);
        image.Freeze();
        disk.Source = ripple.Source = image;
    }

    void UpdateReadings()
    {
        memory.Text = Percent(memoryPercent);
        cpu.Text = Percent(cpuPercent);
        var temperature = sample?.Metrics?.GetValueOrDefault("cpu_temp_c");
        cpuTemperature.Text = temperature is { State: "ok", Value: { } degrees } && double.IsFinite(degrees)
            ? $"{degrees:0.#}°C" : "—";
        cpuTemperature.ToolTip = temperature?.Label ?? "处理器温度";
        commit.Text = Percent(sample?.CommitPercent);
    }

    static string Network(double? bytesPerSecond)
    {
        if (bytesPerSecond is not { } value || !double.IsFinite(value) || value < 0) return "—";
        if (value >= 1073741824) return $"{value / 1073741824:0.#} GB/s";
        return value >= 1048576 ? $"{value / 1048576:0.#} MB/s" : $"{value / 1024:0} KB/s";
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

    IntPtr NoMouseActivation(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int mouseActivate = 0x21, leftButtonDown = 0x0201, activate = 1, noActivate = 3;
        if (message != mouseActivate) return IntPtr.Zero;
        handled = true;
        // 只有拖动区域的左键按下需要前台状态，以便鼠标跨出挂件仍能捕获。
        var trigger = (lParam.ToInt64() >> 16) & 0xffff;
        return new IntPtr(trigger == leftButtonDown && !meterButton.IsMouseOver && PointerOnDock() ? activate : noActivate);
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
        check(FastSpinDuration(-1, 4) == 3 && Math.Abs(FastSpinDuration(.8, 1.2) - .4) < .001 &&
            FastSpinDuration(1, 2) == 1 && FastSpinDuration(2, 3) == 1 && FastSpinDuration(3, 4) == 0,
            "首秒旋转及两段扩散都应按十倍速计时，三秒后恢复基础转速");
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
            check(dock.panelClip.Rect.Size == new Size(PanelWidth, PanelHeight) && dock.memory.Text == "85%" && dock.cpu.Text == "20%" &&
                dock.details.Visibility == Visibility.Visible && dock.rail.Opacity == 0, "实际窗口展开须替换细条并更新读数");
            dock.ApplyNetwork(new(DateTimeOffset.UtcNow, 2 * 1048576, 1024));
            dock.ApplySample(null);
            check(dock.download.Text == "2 MB/s" && dock.upload.Text == "1 KB/s", "挂件网速来自独立快速采样且不被常规采样覆盖");
            var thermal = new SystemSnapshot(DateTimeOffset.UtcNow, null, null, null, null, null, null, null, [],
                Metrics: new() { ["cpu_temp_c"] = new("cpu_temp_c", 58, "°C", "CPU 测温") });
            dock.ApplySample(thermal);
            check(dock.cpuTemperature.Text == "58°C" && Equals(dock.cpuTemperature.ToolTip, "CPU 测温"), "挂件应显示采样到的 CPU 温度及来源");
            thermal.Metrics!["cpu_temp_c"] = new("cpu_temp_c", 58, "°C", State: "stale");
            dock.ApplySample(thermal);
            check(dock.cpuTemperature.Text == "—", "挂件应显示过期的温度为未知");
            dock.ApplyNetwork(null);
            check(dock.download.Text == "—" && dock.upload.Text == "—", "网速等待下一轮或不可用时显示未知");
            var accelerations = 0;
            dock.AccelerateRequested += () => accelerations++;
            dock.ActivateAcceleration();
            dock.ActivateAcceleration();
            check(accelerations == 1, "当前整理过程结束前不得重复触发");
            if (SystemParameters.ClientAreaAnimation)
            {
                check(dock.rendering && dock.diskRotation.Angle > 0, "展开后原生渲染应使太极旋转");
                dock.UpdateAccelerationAnimation(.99);
                check(dock.ripple.Opacity == 0 && dock.accelerationStatus.Visibility == Visibility.Collapsed,
                    "首秒十倍速期间仍只显示太极");
                dock.UpdateAccelerationAnimation(1.5);
                check(dock.ripple.Source == dock.disk.Source && dock.ripple.Opacity == .36 &&
                    dock.rippleScale.ScaleX == 2, "扩散半程应到两倍大小并逐渐淡出");
                var meterOrigin = new Point(dock.Left + dock.panelLeft, dock.Top + dock.panelTop);
                dock.UpdateAccelerationAnimation(2);
                check(dock.rippleScale.ScaleX == 3 && dock.ripple.Opacity == .24 && dock.outerRippleBounds &&
                    meterOrigin == new Point(dock.Left + dock.panelLeft, dock.Top + dock.panelTop),
                    "第三秒开始尺寸和透明度须连续，扩展透明区域时保持面板位置");
                dock.UpdateAccelerationAnimation(2.5);
                check(dock.rippleScale.ScaleX == 4.5 && dock.ripple.Opacity == .12 &&
                    dock.panelLeft + PanelWidth / 2 >= CircleRadius * RippleMaxScale &&
                    dock.panelTop + 14 + CircleCenter >= CircleRadius * RippleMaxScale,
                    "第二段须加速扩散，屏幕内侧和上方留足可显示空间");
                dock.UpdateAccelerationAnimation(3);
                check(dock.rippleScale.ScaleX == RippleMaxScale && dock.ripple.Opacity == 0 && !dock.outerRippleBounds &&
                    dock.Width == PanelWidth + BurstMargin &&
                    meterOrigin == new Point(dock.Left + dock.panelLeft, dock.Top + dock.panelTop),
                    "第三秒扩散到六倍后淡出，恢复面板边界和位置");
            }
            check(dock.accelerationStatus.Text == "整理中…" && dock.diskSurface.Visibility == Visibility.Hidden,
                "先播完动画，等待迟到的整理结果时才替换太极");
            dock.CompleteAcceleration("已释放\n2304.8 MiB");
            check(dock.accelerationStatus.Text == "已释放\n2304.8 MiB" && !dock.rendering && dock.resultTimer.IsEnabled,
                "内存整理结果应在太极原位由独立计时器显示，等待期间不绘制");
            PumpFrames(500);
            check(dock.accelerationStatus.Visibility == Visibility.Visible, "结果应保留一秒");
            PumpFrames(650);
            check(dock.accelerationStatus.Visibility == Visibility.Collapsed && dock.diskSurface.Visibility == Visibility.Visible &&
                dock.accelerationPhase == AccelerationPhase.Idle, "结果结束后恢复太极并重新允许点击");
            dock.bagua.SetActive(true);
            dock.bagua.Advance(.2);
            check(dock.bagua.GlyphCount == 1 && !dock.bagua.IsHitTestVisible, "八卦首帧应发射一枚且不拦截太极点击");
            dock.SetExpanded(false);
            var angle = dock.diskRotation.Angle;
            PumpFrames(380);
            check(dock.Width == RailWidth && dock.Height == RailHeight && !dock.rendering && dock.diskRotation.Angle == angle &&
                dock.details.Visibility == Visibility.Collapsed && dock.ripple.Opacity == 0 && dock.bagua.GlyphCount == 0,
                "收起须停转、取消扩散和八卦并解绑渲染");
            dock.anchorY = dock.workArea.Top - 30;
            dock.SnapDock(dock.workArea.Left);
            check(dock.Left == dock.workArea.Left && dock.Top == dock.workArea.Top, "拖放到左上角应贴左并留在工作区");
            dock.anchorY = dock.workArea.Bottom + 30;
            dock.SnapDock(dock.workArea.Right);
            check(dock.Left == dock.workArea.Right - RailWidth && dock.Top == dock.workArea.Bottom - RailHeight,
                "拖放到右下角应贴右并避开任务栏");
            var openedFromMenu = 0;
            var hiddenFromMenu = 0;
            dock.OpenRequested += () => openedFromMenu++;
            dock.HideRequested += () => hiddenFromMenu++;
            foreach (var item in dock.dockMenu.Items.OfType<MenuItem>())
                item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            check(dock.opened && openedFromMenu == 1 && hiddenFromMenu == 1 && accelerations == 2,
                "收起态菜单应能打开烛龙、沿用内存整理，并发出隐藏挂件请求");
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

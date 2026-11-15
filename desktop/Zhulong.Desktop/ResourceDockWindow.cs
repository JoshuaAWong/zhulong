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
    const double RailWidth = 12, RailHeight = 76, RailTrackHeight = 47, PanelWidth = 62, PanelHeight = 224, MeterTop = 7;
    const double BurstMargin = 38, TransitionSeconds = .22, SpinSeconds = 1, RippleSeconds = 1, OuterRippleSeconds = 1,
        ResultSeconds = 1, BlendSeconds = .32, OuterSpinStart = SpinSeconds + RippleSeconds,
        AccelerationSeconds = OuterSpinStart + OuterRippleSeconds,
        CircleRadius = 22, CircleCenter = 24, RippleMaxScale = 6,
        OuterRippleMargin = CircleRadius * RippleMaxScale - PanelWidth / 2 + 4;
    const string AccelerationHint = "点击太极整理白名单进程内存";
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
    readonly TextBlock memory, cpu, cpuTemperature, commit, download, downloadUnit, upload, uploadUnit, accelerationStatus, waitingStatus;
    readonly Ellipse diskRim;
    readonly Image disk, ripple;
    readonly DockBagua bagua = new(FrozenBrush(Jade));
    readonly Grid diskSurface, feedbackLayer;
    readonly Button meterButton;
    readonly ZhulongContextMenu dockMenu;
    readonly RotateTransform diskRotation = new();
    readonly ScaleTransform rippleScale = new(1, 1);
    readonly ScaleTransform diskBlendScale = new(1, 1), textBlendScale = new(1, 1);
    readonly TranslateTransform railOffset = new();
    readonly RectAnimation move = new();
    readonly DispatcherTimer resultTimer = new() { Interval = TimeSpan.FromSeconds(ResultSeconds) };

    SystemSnapshot? sample;
    double? memoryPercent, cpuPercent;
    Rect workArea = SystemParameters.WorkArea;
    double anchorY, panelTop, panelLeft;
    double angle;
    long previousFrame, accelerationStarted, resultBlendStarted, restoreStarted;
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

    enum AccelerationPhase { Idle, Animating, Waiting, Result, Restoring }

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
            Width = 10, Height = 13, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 0, 0), Fill = FrozenBrush(Gray),
            OpacityMask = JadeMask
        };
        rail.Children.Add(dragon);
        var track = new Grid
        {
            Width = 5, Height = RailTrackHeight, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 22, 0, 0),
            Clip = new RectangleGeometry(new Rect(0, 0, 5, RailTrackHeight), 2.5, 2.5)
        };
        progressTrack = new Rectangle { Fill = TrackBrush, RadiusX = 2.5, RadiusY = 2.5 };
        progress = new Rectangle { Fill = ProgressBrush, RadiusX = 2.5, RadiusY = 2.5, Height = 0, VerticalAlignment = VerticalAlignment.Bottom };
        track.Children.Add(progressTrack);
        track.Children.Add(progress);
        rail.Children.Add(track);
        contents.Children.Add(rail);

        details = new StackPanel
        {
            Width = PanelWidth, Height = PanelHeight, Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center, Opacity = 0, Visibility = Visibility.Collapsed
        };
        diskSurface = new Grid { Width = 48, Height = 48, RenderTransform = diskBlendScale, RenderTransformOrigin = new(.5, .5) };
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
        var meterLayer = new Grid { Height = MeterTop + 48 + 4 };
        var meter = new Grid { Width = PanelWidth - 4, Height = 48 };
        meter.Children.Add(diskSurface);
        feedbackLayer = new Grid
        {
            Visibility = Visibility.Collapsed, Opacity = 0, RenderTransform = textBlendScale,
            RenderTransformOrigin = new Point(.5, .5), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false
        };
        waitingStatus = FeedbackText("整理中…");
        accelerationStatus = FeedbackText("");
        feedbackLayer.Children.Add(waitingStatus);
        feedbackLayer.Children.Add(accelerationStatus);
        meter.Children.Add(feedbackLayer);
        meterButton = new Button
        {
            Width = meter.Width, Height = 48, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, MeterTop, 0, 0), Padding = new Thickness(0),
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Content = meter,
            ToolTip = AccelerationHint
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
        (download, downloadUnit) = AddNetworkMetric("↓");
        (upload, uploadUnit) = AddNetworkMetric("↑");
        contents.Children.Add(details);
        Content = scene;
        resultTimer.Tick += (_, _) => BeginRestore();
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
            progress.Height = next * RailTrackHeight / 100 ?? 0;
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
        (download.Text, downloadUnit.Text) = Network(value?.DownloadBytesPerSecond);
        (upload.Text, uploadUnit.Text) = Network(value?.UploadBytesPerSecond);
    }

    internal void CompleteAcceleration(string text)
    {
        if (accelerationPhase is not (AccelerationPhase.Animating or AccelerationPhase.Waiting)) return;
        if (accelerationResult is not null) return;
        if (feedbackLayer.Opacity * waitingStatus.Opacity > 0) resultBlendStarted = Stopwatch.GetTimestamp();
        accelerationResult = text;
        accelerationStatus.Text = text;
        UpdateRendering();
    }

    static TextBlock FeedbackText(string value)
    {
        var label = Text(value, 10, TextBrush, true);
        label.TextAlignment = TextAlignment.Center;
        label.TextWrapping = TextWrapping.Wrap;
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        label.MaxHeight = 40;
        label.Opacity = 0;
        return label;
    }

    void FinishBurst(long now, bool motion)
    {
        accelerationPhase = AccelerationPhase.Waiting;
        ripple.Opacity = 0;
        if (dragStart is null) SetOuterRippleBounds(false);
        DrawFeedback(1, now, motion);
    }

    void DrawFeedback(double reveal, long now, bool motion = true)
    {
        var mix = accelerationResult is null ? 0 : !motion || resultBlendStarted == 0 ? 1 :
            EaseBlend(Stopwatch.GetElapsedTime(resultBlendStarted, now).TotalSeconds);
        feedbackLayer.Visibility = Visibility.Visible;
        feedbackLayer.Opacity = reveal;
        waitingStatus.Opacity = 1 - mix;
        accelerationStatus.Opacity = mix;
        diskSurface.Opacity = 1 - reveal;
        diskBlendScale.ScaleX = diskBlendScale.ScaleY = 1 - .08 * reveal;
        textBlendScale.ScaleX = textBlendScale.ScaleY = .94 + .06 * reveal;
        if (accelerationPhase == AccelerationPhase.Waiting && mix == 1)
        {
            accelerationPhase = AccelerationPhase.Result;
            meterButton.ToolTip = accelerationResult;
            resultTimer.Start();
        }
    }

    void BeginRestore()
    {
        resultTimer.Stop();
        meterButton.ToolTip = null;
        if (!IsVisible || !SystemParameters.ClientAreaAnimation) { ClearAcceleration(); return; }
        accelerationPhase = AccelerationPhase.Restoring;
        restoreStarted = Stopwatch.GetTimestamp();
        UpdateRendering();
    }

    static double EaseBlend(double elapsed)
    {
        var progress = Math.Clamp(elapsed / BlendSeconds, 0, 1);
        return progress * progress * (3 - 2 * progress);
    }

    void ClearAcceleration()
    {
        resultTimer.Stop();
        accelerationPhase = AccelerationPhase.Idle;
        accelerationResult = null;
        resultBlendStarted = 0;
        accelerationStatus.Text = "";
        meterButton.ToolTip = AccelerationHint;
        feedbackLayer.Visibility = Visibility.Collapsed;
        feedbackLayer.Opacity = 0;
        ripple.Opacity = 0;
        diskSurface.Opacity = 1;
        diskBlendScale.ScaleX = diskBlendScale.ScaleY = 1;
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
        meterButton.ToolTip = null;
        accelerationPhase = AccelerationPhase.Animating;
        accelerationStarted = Stopwatch.GetTimestamp();
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
        if (IsVisible && !motion)
        {
            var now = Stopwatch.GetTimestamp();
            if (accelerationPhase == AccelerationPhase.Animating) FinishBurst(now, false);
            else if (accelerationPhase == AccelerationPhase.Waiting && accelerationResult is not null) DrawFeedback(1, now, false);
            else if (accelerationPhase == AccelerationPhase.Restoring) ClearAcceleration();
        }
        bagua.SetActive(motion && opened && meterButton.IsMouseOver &&
            accelerationPhase == AccelerationPhase.Idle && !dockMenu.IsOpen);
        var animate = motion &&
            (move.Pending || FeedbackMoving ||
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

    bool FeedbackMoving => accelerationPhase is AccelerationPhase.Animating or AccelerationPhase.Restoring ||
        (accelerationPhase == AccelerationPhase.Waiting && accelerationResult is not null);

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
            if (!opened) details.Visibility = Visibility.Collapsed;
            UpdateRendering();
            return;
        }

        long now = Stopwatch.GetTimestamp();
        double dt = Stopwatch.GetElapsedTime(previousFrame, now).TotalSeconds;
        var accelerating = accelerationPhase == AccelerationPhase.Animating;
        if (!move.Pending && !FeedbackMoving && dt < 1d / 34) return;
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
        if (opened && (accelerating || ((accelerationPhase is AccelerationPhase.Idle or AccelerationPhase.Restoring) && memoryPercent.HasValue)))
        {
            var idleSpeed = meterButton.IsMouseOver ? 3 : 1;
            var rotationTime = accelerating ? BoostedRotationTime(
                Stopwatch.GetElapsedTime(accelerationStarted, priorFrame).TotalSeconds,
                Stopwatch.GetElapsedTime(accelerationStarted, now).TotalSeconds, idleSpeed) : dt * idleSpeed;
            angle = (angle + rotationTime * 360 / RotationPeriod(memoryPercent ?? 0)) % 360;
            diskRotation.Angle = angle;
        }
        if (accelerating) UpdateAccelerationAnimation(Stopwatch.GetElapsedTime(accelerationStarted, now).TotalSeconds);
        else if (accelerationPhase == AccelerationPhase.Waiting && accelerationResult is not null) DrawFeedback(1, now);
        else if (accelerationPhase == AccelerationPhase.Restoring)
        {
            var reveal = 1 - EaseBlend(Stopwatch.GetElapsedTime(restoreStarted, now).TotalSeconds);
            if (reveal > 0) DrawFeedback(reveal, now);
            else ClearAcceleration();
        }
        UpdateRendering();
        bagua.Advance(dt);
    }

    void UpdateAccelerationAnimation(double elapsed)
    {
        if (elapsed < SpinSeconds) return;
        double scale, opacity;
        if (elapsed < OuterSpinStart)
        {
            var progress = (elapsed - SpinSeconds) / RippleSeconds;
            var spread = .75 * progress + .25 * progress * progress * (3 - 2 * progress);
            scale = 1 + spread * 2;
            opacity = .48 - .24 * progress;
        }
        else
        {
            if (dragStart is null) SetOuterRippleBounds(true);
            var progress = Math.Min(1, (elapsed - OuterSpinStart) / OuterRippleSeconds);
            // 上一段末端每秒增加 1.5 倍，第三秒以两倍末速从 3 连续扩到 6。
            scale = 3 + (RippleMaxScale - 3) * progress;
            opacity = .24 * (1 - progress);
        }
        rippleScale.ScaleX = rippleScale.ScaleY = scale;
        ripple.Opacity = opacity;
        if (elapsed < AccelerationSeconds - BlendSeconds) return;
        var now = Stopwatch.GetTimestamp();
        if (elapsed >= AccelerationSeconds) FinishBurst(now, true);
        else DrawFeedback(EaseBlend(elapsed - (AccelerationSeconds - BlendSeconds)), now);
    }

    static double BoostedRotationTime(double from, double until, double idleSpeed)
    {
        var boostFrom = Math.Clamp(from, 0, AccelerationSeconds);
        var boostUntil = Math.Clamp(until, 0, AccelerationSeconds);
        var boostSeconds = boostUntil - boostFrom;
        // 从点击到扩散结束，按帧内首尾速度的平均值累计十倍到三十倍的线性转角。
        var averageBoostSpeed = 10 + 20 * ((boostFrom + boostUntil) * .5 / AccelerationSeconds);
        return (until - from - boostSeconds) * idleSpeed + boostSeconds * averageBoostSpeed;
    }

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
        Canvas.SetTop(ripple, panelTop + MeterTop);
        bagua.Place(new Point(panelLeft + PanelWidth / 2, panelTop + MeterTop + CircleCenter), new Size(Width, Height));
        UpdateVisual(move.Progress);
    }

    void UpdateVisual(double value)
    {
        var width = RailWidth + (PanelWidth - RailWidth) * value;
        var height = RailHeight + (PanelHeight - RailHeight) * value;
        var left = expandedBounds && dockRight ? panelLeft + PanelWidth - width : panelLeft;
        var top = expandedBounds ? Math.Clamp(anchorY - height / 2 - Top, panelTop, panelTop + PanelHeight - height) : 0;
        panelClip.Rect = new Rect(left, top, width, height);
        panelClip.RadiusX = panelClip.RadiusY = width / 2;
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

    static (string Amount, string Unit) Network(double? bytesPerSecond)
    {
        if (bytesPerSecond is not { } value || !double.IsFinite(value) || value < 0) return ("—", "");
        if (value >= 1073741824) return ($"{value / 1073741824:0.#}", "GB/s");
        return value >= 1048576 ? ($"{value / 1048576:0.#}", "MB/s") : ($"{value / 1024:0}", "KB/s");
    }

    (TextBlock Amount, TextBlock Unit) AddNetworkMetric(string direction)
    {
        var row = new Grid { Width = 48, HorizontalAlignment = HorizontalAlignment.Center };
        row.RowDefinitions.Add(new() { Height = new GridLength(12) });
        row.RowDefinitions.Add(new() { Height = new GridLength(10) });
        var arrow = Text(direction, 10, MutedBrush);
        arrow.HorizontalAlignment = HorizontalAlignment.Left;
        var amount = Text("—", 10, TextBrush, true);
        amount.HorizontalAlignment = HorizontalAlignment.Center;
        var unit = Text("", 9, MutedBrush);
        unit.HorizontalAlignment = HorizontalAlignment.Center;
        Grid.SetRow(unit, 1);
        row.Children.Add(arrow);
        row.Children.Add(amount);
        row.Children.Add(unit);
        details.Children.Add(row);
        return (amount, unit);
    }

    TextBlock AddMetric(string label)
    {
        var row = new DockPanel { Margin = new Thickness(5, 2, 5, 2), Height = 16 };
        var left = Text(label, 10, MutedBrush);
        DockPanel.SetDock(left, Dock.Left);
        row.Children.Add(left);
        var right = Text("—", 10, TextBrush, true);
        right.TextAlignment = TextAlignment.Right;
        row.Children.Add(new Viewbox
        {
            Child = right, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Right
        });
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
        check(DockBagua.WhiteRingWeight(0) == 1 && DockBagua.WhiteRingWeight(.7) < .001 &&
            DockBagua.WhiteRingWeight(1.4) > .999 && DockBagua.ContactOpacity(23.2) == 3 * DockBagua.RingOpacity &&
            DockBagua.ContactOpacity(18) == 0 && DockBagua.ContactOpacity(28) == 0,
            "悬停光环应交替明暗，字符触及光环的位置峰值为常规光环的三倍");
        check(BoostedRotationTime(-1, 4, 3) == 66 && Math.Abs(BoostedRotationTime(0, .25, 3) - 65d / 24) < .001 &&
            Math.Abs(BoostedRotationTime(1.8, 2.2, 3) - 28d / 3) < .001 &&
            Math.Abs(BoostedRotationTime(2.75, 3, 3) - 175d / 24) < .001 && BoostedRotationTime(3, 4, 3) == 3,
            "点击后三秒从十倍线性增至三十倍，跨帧边界时正确累计角度");
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
            check(dock.panelClip.RadiusX == PanelWidth / 2 && dock.panelClip.RadiusY == PanelWidth / 2 &&
                MeterTop + CircleCenter == PanelWidth / 2, "展开面板的两端圆弧应对称，太极与顶端圆弧同心");
            dock.ApplyNetwork(new(DateTimeOffset.UtcNow, 2 * 1048576, 1024));
            dock.ApplySample(null);
            check(dock.download.Text == "2" && dock.downloadUnit.Text == "MB/s" && Grid.GetRow(dock.downloadUnit) == 1 &&
                dock.upload.Text == "1" && dock.uploadUnit.Text == "KB/s" && Grid.GetRow(dock.uploadUnit) == 1,
                "挂件上传和下载应分行显示读数和单位，常规快照不得覆盖快采样");
            var thermal = new SystemSnapshot(DateTimeOffset.UtcNow, null, null, null, null, null, null, null, [],
                Metrics: new() { ["cpu_temp_c"] = new("cpu_temp_c", 58, "°C", "CPU 测温") });
            dock.ApplySample(thermal);
            check(dock.cpuTemperature.Text == "58°C" && Equals(dock.cpuTemperature.ToolTip, "CPU 测温"), "挂件应显示采样到的 CPU 温度及来源");
            thermal.Metrics!["cpu_temp_c"] = new("cpu_temp_c", 58, "°C", State: "stale");
            dock.ApplySample(thermal);
            check(dock.cpuTemperature.Text == "—", "挂件应显示过期的温度为未知");
            dock.ApplyNetwork(null);
            check(dock.download.Text == "—" && dock.upload.Text == "—" && dock.downloadUnit.Text == "" &&
                dock.uploadUnit.Text == "", "网速未知时应移除旧单位");
            var accelerations = 0;
            dock.AccelerateRequested += () => accelerations++;
            dock.ActivateAcceleration();
            dock.ActivateAcceleration();
            check(accelerations == 1, "当前整理过程结束前不得重复触发");
            if (SystemParameters.ClientAreaAnimation)
            {
                check(dock.rendering && dock.diskRotation.Angle > 0, "展开后原生渲染应使太极旋转");
                dock.UpdateAccelerationAnimation(.99);
                check(dock.ripple.Opacity == 0 && dock.feedbackLayer.Visibility == Visibility.Collapsed,
                    "首秒旋转加速期间仍只显示太极");
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
                    dock.panelTop + MeterTop + CircleCenter >= CircleRadius * RippleMaxScale,
                    "第二段须加速扩散，屏幕内侧和上方留足可显示空间");
                dock.UpdateAccelerationAnimation(2.84);
                check(Math.Abs(dock.diskSurface.Opacity - .5) < .001 && Math.Abs(dock.feedbackLayer.Opacity - .5) < .001 &&
                    dock.ripple.Opacity > 0 && dock.waitingStatus.Opacity == 1 && !dock.resultTimer.IsEnabled,
                    "最后一段扩散中太极与等待文字应交叠可见");
                dock.UpdateAccelerationAnimation(3);
                check(dock.rippleScale.ScaleX == RippleMaxScale && dock.ripple.Opacity == 0 && !dock.outerRippleBounds &&
                    dock.Width == PanelWidth + BurstMargin &&
                    meterOrigin == new Point(dock.Left + dock.panelLeft, dock.Top + dock.panelTop),
                    "第三秒扩散到六倍后淡出，恢复面板边界和位置");
            }
            check(dock.waitingStatus.Text == "整理中…" && dock.feedbackLayer.Opacity == 1 && dock.diskSurface.Opacity == 0 &&
                !dock.resultTimer.IsEnabled, "没有结果时显示等待文字并延后计时");
            dock.CompleteAcceleration("已整理\n2.42 G");
            if (SystemParameters.ClientAreaAnimation)
            {
                check(dock.rendering && !dock.resultTimer.IsEnabled && dock.waitingStatus.Opacity == 1 && dock.accelerationStatus.Opacity == 0,
                    "晚到结果应等文字交叠完再计时");
                PumpFrames(420);
            }
            check(dock.accelerationStatus.Text == "已整理\n2.42 G" && dock.accelerationStatus.Opacity == 1 &&
                dock.waitingStatus.Opacity == 0 && !dock.rendering && dock.resultTimer.IsEnabled,
                "完全可读的整理结果应在太极原位独立计时且无逐帧绘制");
            PumpFrames(500);
            check(dock.feedbackLayer.Opacity == 1 && dock.accelerationStatus.Opacity == 1, "停留期间结果文字应清晰可见");
            dock.BeginRestore();
            if (SystemParameters.ClientAreaAnimation)
            {
                PumpFrames(140);
                check(dock.feedbackLayer.Opacity is > 0 and < 1 && dock.diskSurface.Opacity is > 0 and < 1 &&
                    Math.Abs(dock.feedbackLayer.Opacity + dock.diskSurface.Opacity - 1) < .001, "退场文字与重新显现的太极应同时可见");
                PumpFrames(300);
            }
            check(dock.feedbackLayer.Visibility == Visibility.Collapsed && dock.diskSurface.Opacity == 1 &&
                dock.accelerationPhase == AccelerationPhase.Idle, "结果结束后恢复太极并重新允许点击");
            dock.bagua.SetActive(true);
            dock.bagua.Advance(.2);
            check(dock.bagua.GlyphCount == 1 && !dock.bagua.IsHitTestVisible, "八卦首帧应发射一枚且不拦截太极点击");
            dock.SetExpanded(false);
            var angle = dock.diskRotation.Angle;
            PumpFrames(380);
            check(dock.Width == RailWidth && dock.Height == RailHeight && !dock.rendering && dock.diskRotation.Angle == angle &&
                dock.details.Visibility == Visibility.Collapsed && dock.ripple.Opacity == 0 && dock.bagua.GlyphCount == 0 &&
                dock.panelClip.RadiusX == RailWidth / 2 && dock.panelClip.RadiusY == RailWidth / 2,
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

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Zhulong.P0;

sealed class NativePanel : IPanel
{
    readonly Window window;
    readonly TextBlock caption;
    readonly Curve curve = new();
    readonly TextBlock[] labels = new TextBlock[50];
    bool closed;

    public NativePanel()
    {
        window = new Window
        {
            Title = "烛龙 P0 · 纯 WPF 对照", Width = 1000, Height = 720,
            Left = 100, Top = 100, ShowActivated = false, ShowInTaskbar = false,
            Background = new SolidColorBrush(Color.FromRgb(245, 239, 224))
        };
        var root = new DockPanel { Margin = new Thickness(24) };
        caption = new TextBlock { FontSize = 22, Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(caption, Dock.Top);
        root.Children.Add(caption);
        curve.Height = 230;
        curve.Margin = new Thickness(0, 0, 0, 12);
        DockPanel.SetDock(curve, Dock.Top);
        root.Children.Add(curve);
        var list = new StackPanel();
        for (int i = 0; i < labels.Length; i++)
        {
            labels[i] = new TextBlock { FontSize = 15, Height = 27, Padding = new Thickness(8, 2, 8, 2) };
            list.Children.Add(labels[i]);
        }
        root.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        window.Content = root;
    }

    public Task OpenAsync() { window.Show(); return Task.CompletedTask; }

    public async Task RenderAsync(Snapshot snapshot)
    {
        caption.Text = $"纯 WPF · 300 点 / 50 行 · 第 {snapshot.Sequence} 次刷新";
        curve.Points = snapshot.Points;
        curve.InvalidateVisual();
        for (int i = 0; i < labels.Length; i++)
            labels[i].Text = $"{snapshot.Rows[i].Name}                                      {snapshot.Rows[i].Value:0}%";
        // 两次合成回调是刷新代理指标，不等同物理屏幕已显示。
        var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int frames = 0;
        EventHandler handler = (_, _) => { if (++frames >= 2) rendered.TrySetResult(); };
        CompositionTarget.Rendering += handler;
        try { await rendered.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { CompositionTarget.Rendering -= handler; }
    }

    public Task CaptureAsync(string path)
    {
        // 由原型导出自身真实渲染，正式性能测量不开启截图。
        var image = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),
            (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        image.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return Task.CompletedTask;
    }

    public Task CloseAsync()
    {
        if (!closed) { closed = true; window.Content = null; window.Close(); }
        return Task.CompletedTask;
    }

    sealed class Curve : FrameworkElement
    {
        public double[] Points { get; set; } = [];
        protected override void OnRender(DrawingContext context)
        {
            double width = ActualWidth, height = ActualHeight;
            context.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            var gridPen = new Pen(Brushes.LightGray, 1);
            for (int i = 0; i <= 4; i++)
                context.DrawLine(gridPen, new Point(0, height * i / 4), new Point(width, height * i / 4));
            if (Points.Length < 2) return;
            var geometry = new StreamGeometry();
            using (var path = geometry.Open())
            {
                path.BeginFigure(new Point(0, height * (1 - Points[0] / 100)), false, false);
                for (int i = 1; i < Points.Length; i++)
                    path.LineTo(new Point(width * i / (Points.Length - 1), height * (1 - Points[i] / 100)), true, false);
            }
            geometry.Freeze();
            context.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromRgb(160, 55, 45)), 2), geometry);
        }
    }
}

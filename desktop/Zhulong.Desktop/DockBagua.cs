using System.Windows;
using System.Windows.Media;

namespace Zhulong.Desktop;

// 沿用总览的八卦符号、发射节拍和淡入淡出节点，半径适配贴边窗口。
internal sealed class DockBagua : FrameworkElement
{
    const double EdgeMargin = 13;
    static readonly Geometry[] Glyphs = [.. new[] { 7, 3, 5, 1, 6, 2, 4, 0 }.Select(MakeGlyph)];
    readonly List<Flight> flights = new(10);
    readonly Brush ink;
    Point center;
    double elapsed, nextEmission;

    internal DockBagua(Brush ink)
    {
        this.ink = ink;
        IsHitTestVisible = false;
    }

    internal bool Active { get; private set; }
    internal int GlyphCount => flights.Count;

    internal void Place(Point origin, Size bounds)
    {
        center = origin;
        Width = bounds.Width;
        Height = bounds.Height;
    }

    internal void SetActive(bool active)
    {
        if (Active == active) return;
        Active = active;
        if (active)
        {
            elapsed = nextEmission = 0;
            return;
        }
        flights.Clear();
        InvalidateVisual();
    }

    internal void Advance(double seconds)
    {
        if (!Active) return;
        elapsed += seconds;
        flights.RemoveAll(flight => elapsed - flight.Born >= flight.Duration);
        if (elapsed >= nextEmission)
        {
            var random = Random.Shared;
            if (flights.Count < 10)
                flights.Add(new(elapsed, .95 + random.NextDouble() * .65, random.Next(8),
                    random.NextDouble() * 360, 18 + random.NextDouble() * 42,
                    50 * (.8 + random.NextDouble() * .45), .85 + random.NextDouble() * .65));
            nextEmission = elapsed + .17 + random.NextDouble() * .22;
        }
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext context)
    {
        foreach (var flight in flights)
        {
            var progress = (elapsed - flight.Born) / flight.Duration;
            var (distance, opacity) = Keyframe(progress);
            var degrees = flight.Angle + flight.Turn * progress;
            var radians = degrees * Math.PI / 180;
            var horizontal = Math.Cos(radians);
            var vertical = Math.Sin(radians);
            var edgeX = horizontal < 0 ? center.X : Width - center.X;
            var edgeY = vertical < 0 ? center.Y : Height - center.Y;
            var radius = Math.Min(flight.Radius, Math.Min((edgeX - EdgeMargin) / Math.Abs(horizontal),
                (edgeY - EdgeMargin) / Math.Abs(vertical)));
            var scale = Mix(flight.Scale, .08, progress);
            var transform = Matrix.Identity;
            transform.Scale(scale, scale);
            transform.Rotate(degrees + 90);
            transform.Translate(center.X + horizontal * radius * distance, center.Y + vertical * radius * distance);
            context.PushOpacity(opacity);
            context.PushTransform(new MatrixTransform(transform));
            context.DrawGeometry(ink, null, Glyphs[flight.Symbol]);
            context.Pop();
            context.Pop();
        }
    }

    static (double Distance, double Opacity) Keyframe(double progress)
    {
        if (progress <= .18)
        {
            var fraction = progress / .18;
            return (Mix(1, .84, fraction), Mix(0, .58, fraction));
        }
        if (progress <= .72)
        {
            var fraction = (progress - .18) / .54;
            return (Mix(.84, .24, fraction), Mix(.58, .42, fraction));
        }
        var end = (1 - progress) / .28;
        return (.24 * end, .42 * end);
    }

    static Geometry MakeGlyph(int lines)
    {
        var geometry = new GeometryGroup();
        for (var row = 0; row < 3; row++)
        {
            var y = -4 + row * 3;
            if ((lines & (4 >> row)) != 0) geometry.Children.Add(new RectangleGeometry(new Rect(-6, y, 12, 1.5)));
            else
            {
                geometry.Children.Add(new RectangleGeometry(new Rect(-6, y, 4.8, 1.5)));
                geometry.Children.Add(new RectangleGeometry(new Rect(1.2, y, 4.8, 1.5)));
            }
        }
        geometry.Freeze();
        return geometry;
    }

    static double Mix(double from, double to, double fraction) => from + (to - from) * fraction;
    readonly record struct Flight(double Born, double Duration, int Symbol, double Angle, double Turn, double Radius, double Scale);
}

using System.Windows;
using System.Windows.Media;

namespace Zhulong.Desktop;

// 沿用总览的八卦符号、发射节拍和淡入淡出节点，半径适配贴边窗口。
internal sealed class DockBagua : FrameworkElement
{
    const double EdgeMargin = 13;
    const double RingRadius = 23.2, RingPeriod = 1.4, ContactReach = 4;
    internal const double RingOpacity = .3;
    static readonly Geometry[] Glyphs = [.. new[] { 7, 3, 5, 1, 6, 2, 4, 0 }.Select(MakeGlyph)];
    static readonly Pen WhiteRing = RingPen(Colors.White, 1.6), BlackRing = RingPen(Colors.Black, 1.6),
        ContactRing = RingPen(Colors.White, 2);
    static readonly Geometry ContactArc = MakeContactArc();
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
        if (!Active) return;
        var white = WhiteRingWeight(elapsed);
        var ringAlpha = RingOpacity * Math.Min(1, elapsed / .15);
        context.PushOpacity(ringAlpha * (1 - white));
        context.DrawEllipse(null, BlackRing, center, RingRadius, RingRadius);
        context.Pop();
        context.PushOpacity(ringAlpha * white);
        context.DrawEllipse(null, WhiteRing, center, RingRadius, RingRadius);
        context.Pop();
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
            var radialDistance = radius * distance;
            var scale = Mix(flight.Scale, .08, progress);
            var transform = Matrix.Identity;
            transform.Scale(scale, scale);
            transform.Rotate(degrees + 90);
            transform.Translate(center.X + horizontal * radialDistance, center.Y + vertical * radialDistance);
            context.PushOpacity(opacity);
            context.PushTransform(new MatrixTransform(transform));
            context.DrawGeometry(ink, null, Glyphs[flight.Symbol]);
            context.Pop();
            context.Pop();
            var contact = ContactOpacity(radialDistance);
            if (contact <= 0) continue;
            transform = Matrix.Identity;
            transform.Rotate(degrees);
            transform.Translate(center.X, center.Y);
            context.PushOpacity(contact);
            context.PushTransform(new MatrixTransform(transform));
            context.DrawGeometry(null, ContactRing, ContactArc);
            context.Pop();
            context.Pop();
        }
    }

    internal static double WhiteRingWeight(double seconds) => .5 + .5 * Math.Cos(2 * Math.PI * seconds / RingPeriod);

    internal static double ContactOpacity(double radius) =>
        3 * RingOpacity * Math.Max(0, 1 - Math.Abs(radius - RingRadius) / ContactReach);

    static Pen RingPen(Color color, double thickness)
    {
        var pen = new Pen(new SolidColorBrush(color), thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        return pen;
    }

    static Geometry MakeContactArc()
    {
        const double halfArc = 3;
        var x = Math.Sqrt(RingRadius * RingRadius - halfArc * halfArc);
        var arc = new StreamGeometry();
        using (var path = arc.Open())
        {
            path.BeginFigure(new Point(x, -halfArc), false, false);
            path.ArcTo(new Point(x, halfArc), new Size(RingRadius, RingRadius), 0, false, SweepDirection.Clockwise, true, false);
        }
        arc.Freeze();
        return arc;
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

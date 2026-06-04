using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Docklys.ModuleContracts;

namespace DocklysPatterns;

// A triangular tessellation. Each triangle tints toward purple based on how close
// the cursor is to its center, over thin ink edges. Hover reactive, no trails.
public sealed class TrianglesPattern : IPattern
{
    public string PatternName => "Triangles";
    public string PatternVersion => "1.0";
    public string UniquePatternId { get; private set; } = "pattern.triangles";
    public void SetPatternId(string uniquePatternId) => UniquePatternId = uniquePatternId;

    public Control CreateView(PatternContext ctx) => new TrianglesView(ctx);
}

internal sealed class TrianglesView : Control, IPatternInteraction
{
    private readonly Color _c1, _c2, _ink;
    private static readonly Color Purple = Color.FromRgb(0xB0, 0x61, 0xFF);
    private Point? _pointer;

    private const double S = 46;        // cell size
    private const double Reach = 150;

    public TrianglesView(PatternContext ctx)
    {
        _c1 = ctx.Color1; _c2 = ctx.Color2; _ink = ctx.Color3;
        ClipToBounds = true;
    }

    public void OnPointerMoved(double? x, double? y)
    {
        _pointer = (x is { } px && y is { } py) ? new Point(px, py) : null;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var rect = new Rect(Bounds.Size);
        if (rect.Width <= 0 || rect.Height <= 0) return;
        PatternFx.FillBackground(context, rect, _c1, _c2);

        Point? cursor = _pointer is { } p ? new Point(p.X * rect.Width, p.Y * rect.Height) : null;
        var edge = new Pen(new SolidColorBrush(Color.FromArgb(36, _ink.R, _ink.G, _ink.B)), 1);

        for (double gy = 0; gy < rect.Height; gy += S)
            for (double gx = 0; gx < rect.Width; gx += S)
            {
                var tl = new Point(gx, gy);
                var tr = new Point(gx + S, gy);
                var bl = new Point(gx, gy + S);
                var br = new Point(gx + S, gy + S);
                // Split each cell along the TL→BR diagonal into two triangles.
                DrawTri(context, edge, cursor, tl, tr, br);
                DrawTri(context, edge, cursor, tl, br, bl);
            }
    }

    private void DrawTri(DrawingContext context, Pen edge, Point? cursor, Point a, Point b, Point c)
    {
        double cx = (a.X + b.X + c.X) / 3.0;
        double cy = (a.Y + b.Y + c.Y) / 3.0;
        double glow = PatternFx.Proximity(cursor, cx, cy, Reach);

        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(a, true);
            g.LineTo(b);
            g.LineTo(c);
            g.EndFigure(true);
        }

        IBrush? fill = glow > 0.001
            ? new SolidColorBrush(Color.FromArgb((byte)(150 * glow), Purple.R, Purple.G, Purple.B))
            : null;
        context.DrawGeometry(fill, edge, geo);
    }
}

internal static class PatternFx
{
    public static void FillBackground(DrawingContext context, Rect rect, Color c1, Color c2)
    {
        var bg = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative)
        };
        bg.GradientStops.Add(new GradientStop(c1, 0));
        bg.GradientStops.Add(new GradientStop(c2, 1));
        context.FillRectangle(bg, rect);
    }

    public static double Proximity(Point? cursor, double x, double y, double reach)
    {
        if (cursor is not { } c) return 0;
        double dx = x - c.X, dy = y - c.Y;
        double t = Math.Clamp(1 - Math.Sqrt(dx * dx + dy * dy) / reach, 0, 1);
        return t * t;
    }

    public static Color Lerp(Color a, Color b, double t)
    {
        byte L(byte from, byte to) => (byte)(from + (to - from) * t);
        return Color.FromRgb(L(a.R, b.R), L(a.G, b.G), L(a.B, b.B));
    }
}

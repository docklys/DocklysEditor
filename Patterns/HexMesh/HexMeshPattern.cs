using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Docklys.ModuleContracts;

namespace DocklysPatterns;

// A honeycomb of hexagon outlines. Hexes near the cursor light up purple — their
// stroke brightens and thickens by proximity. Pure hover reactivity, no trails.
public sealed class HexMeshPattern : IPattern
{
    public string PatternName => "Hex Mesh";
    public string PatternVersion => "1.0";
    public string UniquePatternId { get; private set; } = "pattern.hexmesh";
    public void SetPatternId(string uniquePatternId) => UniquePatternId = uniquePatternId;

    public Control CreateView(PatternContext ctx) => new HexMeshView(ctx);
}

internal sealed class HexMeshView : Control, IPatternInteraction
{
    private readonly Color _c1, _c2, _ink;
    private static readonly Color Purple = Color.FromRgb(0xB0, 0x61, 0xFF);
    private Point? _pointer;

    private const double R = 22;        // hex radius (center → vertex)
    private const double Reach = 150;

    public HexMeshView(PatternContext ctx)
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

        double hStep = Math.Sqrt(3) * R;   // horizontal gap between centers
        double vStep = 1.5 * R;            // vertical gap between rows
        int row = 0;
        for (double cy = 0; cy < rect.Height + R; cy += vStep, row++)
        {
            double offset = (row % 2 == 0) ? 0 : hStep / 2;
            for (double cx = -hStep + offset; cx < rect.Width + hStep; cx += hStep)
            {
                double glow = PatternFx.Proximity(cursor, cx, cy, Reach);
                var color = PatternFx.Lerp(_ink, Purple, glow);
                byte alpha = (byte)(55 + 170 * glow);
                var pen = new Pen(new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B)), 1 + glow * 1.6);
                DrawHex(context, cx, cy, pen);
            }
        }
    }

    private static void DrawHex(DrawingContext context, double cx, double cy, Pen pen)
    {
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            for (int i = 0; i < 6; i++)
            {
                double ang = Math.PI / 180 * (60 * i - 90); // pointy-top
                var pt = new Point(cx + R * Math.Cos(ang), cy + R * Math.Sin(ang));
                if (i == 0) g.BeginFigure(pt, false);
                else g.LineTo(pt);
            }
            g.EndFigure(true);
        }
        context.DrawGeometry(null, pen, geo);
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

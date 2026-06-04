using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Docklys.ModuleContracts;

namespace DocklysPatterns;

// A faint graph-paper grid whose intersection nodes light up purple near the
// cursor. The lines stay subtle; the plus-shaped nodes flare on hover. No trails.
public sealed class GridGlowPattern : IPattern
{
    public string PatternName => "Grid Glow";
    public string PatternVersion => "1.0";
    public string UniquePatternId { get; private set; } = "pattern.gridglow";
    public void SetPatternId(string uniquePatternId) => UniquePatternId = uniquePatternId;

    public Control CreateView(PatternContext ctx) => new GridGlowView(ctx);
}

internal sealed class GridGlowView : Control, IPatternInteraction
{
    private readonly Color _c1, _c2, _ink;
    private static readonly Color Purple = Color.FromRgb(0xB0, 0x61, 0xFF);
    private Point? _pointer;

    private const double Step = 32;
    private const double Reach = 130;

    public GridGlowView(PatternContext ctx)
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

        // Subtle base grid.
        var line = new Pen(new SolidColorBrush(Color.FromArgb(28, _ink.R, _ink.G, _ink.B)), 1);
        for (double x = Step; x < rect.Width; x += Step)
            context.DrawLine(line, new Point(x, 0), new Point(x, rect.Height));
        for (double y = Step; y < rect.Height; y += Step)
            context.DrawLine(line, new Point(0, y), new Point(rect.Width, y));

        // Glowing plus nodes at intersections near the cursor.
        Point? cursor = _pointer is { } p ? new Point(p.X * rect.Width, p.Y * rect.Height) : null;
        for (double y = Step; y < rect.Height; y += Step)
            for (double x = Step; x < rect.Width; x += Step)
            {
                double glow = PatternFx.Proximity(cursor, x, y, Reach);
                if (glow <= 0.001) continue;
                var color = PatternFx.Lerp(_ink, Purple, glow);
                var pen = new Pen(new SolidColorBrush(Color.FromArgb((byte)(60 + 180 * glow), color.R, color.G, color.B)), 1 + glow * 1.5);
                double arm = 3 + glow * 5;
                context.DrawLine(pen, new Point(x - arm, y), new Point(x + arm, y));
                context.DrawLine(pen, new Point(x, y - arm), new Point(x, y + arm));
            }
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

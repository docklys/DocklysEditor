using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Docklys.ModuleContracts;

namespace DocklysPatterns;

// A calm grid of dots that light up purple as the cursor passes near them.
// Each dot blends from the skin's ink color toward purple and grows by how close
// the pointer is — a hover-proximity glow, no trails. Repaints on pointer move.
public sealed class ReactiveDotsPattern : IPattern
{
    public string PatternName => "Reactive Dots";
    public string PatternVersion => "1.0";
    public string UniquePatternId { get; private set; } = "pattern.reactivedots";
    public void SetPatternId(string uniquePatternId) => UniquePatternId = uniquePatternId;

    public Control CreateView(PatternContext ctx) => new ReactiveDotsView(ctx);
}

internal sealed class ReactiveDotsView : Control, IPatternInteraction
{
    private readonly Color _c1, _c2, _ink;
    private static readonly Color Purple = Color.FromRgb(0xB0, 0x61, 0xFF);
    private Point? _pointer;

    private const double Spacing = 26;
    private const double BaseRadius = 2;
    private const double Reach = 120;

    public ReactiveDotsView(PatternContext ctx)
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

        for (double y = Spacing / 2; y < rect.Height; y += Spacing)
            for (double x = Spacing / 2; x < rect.Width; x += Spacing)
            {
                double glow = PatternFx.Proximity(cursor, x, y, Reach);
                var color = PatternFx.Lerp(_ink, Purple, glow);
                byte alpha = (byte)(110 + 145 * glow);
                var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
                context.DrawEllipse(brush, null, new Point(x, y), BaseRadius + glow * 4.5, BaseRadius + glow * 4.5);
            }
    }
}

// Shared helpers so every pattern shares the same look-and-feel.
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

    // 0 = far from the cursor (resting), 1 = right under it. Eased so only nearby
    // cells flare brightly. Returns 0 when the pointer is away.
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

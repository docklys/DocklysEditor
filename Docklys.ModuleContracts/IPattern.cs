using Avalonia.Controls;
using Avalonia.Media;

namespace Docklys.ModuleContracts;

// A Pattern is a pluggable, optionally animated window background — the visual
// sibling of IModule. The host loads pattern DLLs from %AppData%/Docklys/Pattern
// and mounts one per window (Main / Settings can each pick their own). Unlike a
// brush, a pattern owns a live Control so it can animate and react to the pointer.
public interface IPattern
{
    string PatternName { get; }
    string PatternVersion { get; }
    string UniquePatternId { get; }

    void SetPatternId(string uniquePatternId);

    // Build the background control. The host places it as the bottom layer of a
    // window (inside the GrainBackground border, below any image overlay). The
    // returned control owns its own animation loop. ctx supplies the active
    // skin's colors so the pattern follows the host theme.
    Control CreateView(PatternContext ctx);
}

// Implemented by patterns that respond to the mouse. The host forwards the
// pointer position over the window as normalized 0..1 coordinates, or null when
// the pointer leaves. Patterns that don't implement this stay static.
public interface IPatternInteraction
{
    void OnPointerMoved(double? x, double? y);
}

// Skin color tokens + global tuning resolved by the host and handed to a pattern
// at view-creation time. Mirrors the keys in SkinKeys so patterns adapt to skins.
public sealed class PatternContext
{
    // ColorColorBackground
    public Color Color1 { get; set; } = Colors.Gray;
    // ColorColor2Background
    public Color Color2 { get; set; } = Colors.DarkGray;
    // ColorColor3Background — the pattern "ink" color (dots/lines/etc.).
    public Color Color3 { get; set; } = Colors.White;
    // ColorAccent
    public Color Accent { get; set; } = Colors.DodgerBlue;

    // Global density / strength slider value (host-wide), 0..0.2 by convention.
    public double Density { get; set; } = 0.03;
}

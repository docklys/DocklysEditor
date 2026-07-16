using System;
using System.Diagnostics;
using System.Linq;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;

namespace RunModule;

// "Real size" feature — the zoom slider magnetically locks onto the actual
// pixel sizes the active module would render at in the main Docklys app.
//
// In the main app a module's on-screen size is driven by the user's
// "Tiles per row" setting (3–8). The grid math lives in
// Docklys\Dockly\Views\MainWindow.axaml.cs; the constants below mirror it
// exactly so the editor preview matches reality.
//
// Why a single uniform scale is enough: the editor draws a module at
// PixelsForTiles(W) = 120*W - 10 (a 110 px tile + 10 px gap = 120 px step).
// The main app uses tile size T and gap g = T * GapToTileRatio, i.e. a step
// of T*(1+ratio). Both share the same 110:120 (= 11:12) tile:step ratio
// whenever the gap isn't capped, so scaling the whole preview by T/110
// reproduces the main-app width AND height for any tile footprint. For the
// 3- and 4-column configs the gap is capped, so multi-tile modules differ by
// a few px — close enough to "see what it looks like in the app".
public partial class MainWindow
{
    // Keep the snap logic aligned with the editor slider's minimum value.
    // Without this, the 4-column "real size" preset (98.8%) can bypass the
    // slider's visual floor and still be applied to the preview.
    // Four to eight tiles per row are legitimately below 100% in Docklys.
    // Keep every real layout available to the slider and cycle command.
    private const double MinimumZoomPercent = 25.0;

    // --- Mirror of the main app's grid constants (Docklys MainWindow.axaml.cs) ---
    private const double MainApp_FixedSideGap = 20.0;
    private const double MainApp_GapToTileRatio = 10.0 / 110.0;
    private const double MainApp_BaseGridWidth = 498.0;
    private const int MainApp_CapColumns = 5;

    // The editor's per-tile baseline: PixelsForTiles(1) = 120*1 - 10 = 110.
    private const double EditorTileBasePx = 110.0;

    // Tiles-per-row configurations the main app exposes (MinTilesPerRow..MaxTilesPerRow).
    private static readonly int[] MainAppColumnConfigs = { 3, 4, 5, 6, 7, 8 };

    // Magnetic snap with hysteresis: the thumb is *captured* onto a config size
    // once it drags within CaptureThreshold of it, and stays *held* there until
    // the drag pulls further than ReleaseThreshold away. Release > capture makes
    // the lock feel firm (it won't jitter off) while still letting you pull free.
    private const double ZoomCaptureThreshold = 3.0;
    private const double ZoomReleaseThreshold = 5.0;

    // Re-entrancy guard: we set Slider.Value from inside ValueChanged when snapping.
    private bool _suppressZoomChange;
    // Which column config the slider is currently locked onto (null = free zoom).
    private int? _zoomSnapColumns;

    // Tile size (px) the main app gives each tile at the given Tiles-per-row count.
    // Faithful re-implementation of GenerateTiles()'s tile-size block.
    internal static double MainAppTileSizePx(int columns)
    {
        double inner = MainApp_BaseGridWidth - 2 * MainApp_FixedSideGap;

        double refTile = inner / (MainApp_CapColumns + (MainApp_CapColumns - 1) * MainApp_GapToTileRatio);
        double capGap = refTile * MainApp_GapToTileRatio;

        double tile = inner / (columns + (columns - 1) * MainApp_GapToTileRatio);
        double gap = tile * MainApp_GapToTileRatio;
        if (gap > capGap)
        {
            gap = capGap;
            tile = (inner - (columns - 1) * gap) / columns;
        }
        return tile;
    }

    // Zoom percentage that makes the editor preview match the main app at `columns` tiles/row.
    internal static double MainAppZoomPercent(int columns) =>
        MainAppTileSizePx(columns) / EditorTileBasePx * 100.0;

    // Resolve where the thumb should rest given the raw dragged-to `value` and
    // the config it's currently locked onto (`held`). Applies hysteresis:
    //   • if held and still within ReleaseThreshold → stay locked;
    //   • otherwise capture the nearest config within CaptureThreshold;
    //   • else free-float at `value`.
    private static (double value, int? columns) ResolveZoom(double value, int? held)
    {
        value = Math.Max(MinimumZoomPercent, value);

        if (held is int hc && MainAppZoomPercent(hc) >= MinimumZoomPercent &&
            Math.Abs(MainAppZoomPercent(hc) - value) <= ZoomReleaseThreshold)
            return (MainAppZoomPercent(hc), hc);

        int? bestCols = null;
        double bestDist = ZoomCaptureThreshold;
        double bestVal = value;
        foreach (var c in MainAppColumnConfigs)
        {
            double zoom = MainAppZoomPercent(c);
            if (zoom < MinimumZoomPercent) continue;

            double d = Math.Abs(zoom - value);
            if (d <= bestDist)
            {
                bestDist = d;
                bestCols = c;
                bestVal = zoom;
            }
        }
        return (bestCols.HasValue ? bestVal : value, bestCols);
    }

    // Configure the slider's snap ticks once the window is up. Called from MainWindow_Loaded.
    private void InitializeMainAppSizeSnaps()
    {
        var slider = this.FindControl<Slider>("ZoomSlider");
        if (slider == null) return;
        try
        {
            slider.Ticks = new AvaloniaList<double>(MainAppColumnConfigs
                .Select(MainAppZoomPercent)
                .Where(zoom => zoom >= MinimumZoomPercent));
            slider.TickPlacement = TickPlacement.Outside;
            slider.IsSnapToTickEnabled = false; // we do our own magnetic snap
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RealSize] tick init failed: {ex.Message}");
        }
    }

    private void UpdateZoomLabel(double percent, int? columns)
    {
        var lbl = this.FindControl<TextBlock>("ZoomLabel");
        if (lbl != null)
            lbl.Text = columns.HasValue
            ? $"{(int)Math.Round(percent)}% · {columns}/row"
            : $"{(int)Math.Round(percent)}%";

        var layoutButton = this.FindControl<Button>("MainAppSizeButton");
        if (layoutButton != null)
            layoutButton.Content = columns.HasValue
                ? $"{columns} tiles / row"
                : "Dock tile layout";
    }

    // "⊞ Real size" button: step to the next Tiles-per-row config and lock the
    // slider onto its real main-app size.
    private void CycleMainAppSize_Click(object? sender, RoutedEventArgs e)
    {
        var slider = this.FindControl<Slider>("ZoomSlider");
        if (slider == null) return;

        int idx;
        if (_zoomSnapColumns.HasValue)
        {
            idx = Array.IndexOf(MainAppColumnConfigs, _zoomSnapColumns.Value);
            do
            {
                idx = (idx + 1) % MainAppColumnConfigs.Length;
            }
            while (MainAppZoomPercent(MainAppColumnConfigs[idx]) < MinimumZoomPercent);
        }
        else
        {
            // Not locked on anything — jump to the config nearest the current zoom.
            idx = 0;
            double bestDist = double.MaxValue;
            for (int i = 0; i < MainAppColumnConfigs.Length; i++)
            {
                double zoom = MainAppZoomPercent(MainAppColumnConfigs[i]);
                if (zoom < MinimumZoomPercent) continue;

                double d = Math.Abs(zoom - slider.Value);
                if (d < bestDist) { bestDist = d; idx = i; }
            }
        }

        // Setting Value re-enters ValueChanged, which snaps + applies + labels.
        slider.Value = MainAppZoomPercent(MainAppColumnConfigs[idx]);
    }

    private void ZoomSlider_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressZoomChange) return;
        try
        {
            var (val, cols) = ResolveZoom(e.NewValue, _zoomSnapColumns);
            if (sender is Slider slider && Math.Abs(val - e.NewValue) > 0.001)
            {
                _suppressZoomChange = true;
                slider.Value = val;
                _suppressZoomChange = false;
            }

            _zoomSnapColumns = cols;
            UpdateZoomLabel(val, cols);
            ApplyZoomToActiveModule(val);
        }
        catch (Exception ex)
        {
            _suppressZoomChange = false;
            Debug.WriteLine($"Zoom change failed: {ex}");
        }
    }
}

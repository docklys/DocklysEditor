using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace RunModule;

// "Change Tile Size" — reads the active module's current TileWidth/TileHeight
// from source, shows the same size-picker dialog as Create Module (but
// pre-populated), rewrites the source files, rebuilds, and hot-reloads.
public partial class MainWindow
{
    private static readonly Regex TileWidthRegex  = new(@"TileWidth\s*=>\s*(\d+)", RegexOptions.Compiled);
    private static readonly Regex TileHeightRegex = new(@"TileHeight\s*=>\s*(\d+)", RegexOptions.Compiled);

    private async void ChangeTileSize_Click(object? sender, RoutedEventArgs e)
    {
        if (_catalog.Count == 0) return;
        var entry = _catalog[_currentIndex];
        var projFolder = Path.GetDirectoryName(entry.CsprojPath)!;

        var (currentW, currentH) = ReadCurrentTileSize(projFolder);

        var spec = await PromptForTileSize(currentW, currentH);
        if (spec == null) return;

        if (spec.TileWidth == currentW && spec.TileHeight == currentH)
        {
            await ShowMessageDialog("No change",
                $"The tile size is already {currentW}×{currentH}. No files were modified.");
            return;
        }

        try
        {
            ApplyTileSizeToSourceFiles(projFolder, currentW, currentH, spec.TileWidth, spec.TileHeight);
        }
        catch (Exception ex)
        {
            await ShowMessageDialog("Resize failed",
                $"Failed to update source files:\n\n{ex.GetType().Name}: {ex.Message}");
            return;
        }

        var buildNote = await TryBuildProject(entry.CsprojPath);
        ReloadCatalogAndSelect(entry.FolderName);

        var newPxW = PixelsForTiles(spec.TileWidth);
        var newPxH = PixelsForTiles(spec.TileHeight);
        var msg = $"'{entry.FolderName}' resized to {spec.TileWidth}×{spec.TileHeight} ({newPxW}×{newPxH} px).";
        if (buildNote != null)
            msg += $"\n\nBuild note: {buildNote}\nFix the error and click ↺ Reload to refresh.";
        await ShowMessageDialog("Tile size updated", msg);
    }

    // Read the current TileWidth / TileHeight from any .cs file in the
    // project folder. Returns (1, 1) as safe defaults if nothing is found.
    private static (int width, int height) ReadCurrentTileSize(string projFolder)
    {
        int w = 1, h = 1;
        foreach (var file in Directory.EnumerateFiles(projFolder, "*.cs", SearchOption.AllDirectories))
        {
            try
            {
                var text = File.ReadAllText(file);
                var mw = TileWidthRegex.Match(text);
                var mh = TileHeightRegex.Match(text);
                if (mw.Success) w = int.Parse(mw.Groups[1].Value);
                if (mh.Success) h = int.Parse(mh.Groups[1].Value);
                if (mw.Success || mh.Success) break;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TileSize] read failed for {file}: {ex.Message}");
            }
        }
        return (w, h);
    }

    // Rewrite Width/Height in AXAML files and TileWidth/TileHeight in CS files.
    // Mirrors what CloneDefaultModuleInto does, but targeting specific old values
    // rather than the fixed template defaults.
    private static void ApplyTileSizeToSourceFiles(
        string projFolder,
        int oldW, int oldH,
        int newW, int newH)
    {
        var oldPxW = PixelsForTiles(oldW);
        var oldPxH = PixelsForTiles(oldH);
        var newPxW = PixelsForTiles(newW);
        var newPxH = PixelsForTiles(newH);

        foreach (var file in Directory.EnumerateFiles(projFolder, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(projFolder, file);
            if (rel.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                rel.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                continue;
            if (IsBinary(file)) continue;
            try
            {
                var text = File.ReadAllText(file);
                var rewritten = text
                    .Replace($"Width=\"{oldPxW}\"",  $"Width=\"{newPxW}\"")
                    .Replace($"Height=\"{oldPxH}\"", $"Height=\"{newPxH}\"")
                    .Replace($"TileWidth => {oldW};",  $"TileWidth => {newW};")
                    .Replace($"TileHeight => {oldH};", $"TileHeight => {newH};");
                if (rewritten != text) File.WriteAllText(file, rewritten);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TileSize] rewrite failed for {file}: {ex.Message}");
            }
        }

        // Delete the Avalonia binary resource cache so the next build
        // regenerates it from the updated AXAML. Without this the stale
        // obj/*/Avalonia/resources bundle (which embeds raw AXAML text
        // including the old Width/Height values) causes AVLN2000.
        var objDir = Path.Combine(projFolder, "obj");
        if (Directory.Exists(objDir))
        {
            foreach (var avDir in Directory.EnumerateDirectories(objDir, "Avalonia", SearchOption.AllDirectories))
            {
                try { Directory.Delete(avDir, recursive: true); }
                catch (Exception ex) { Debug.WriteLine($"[TileSize] Avalonia cache delete failed: {ex.Message}"); }
            }
        }
    }

    // Same size-picker dialog as PromptForNewModuleSpec but without the name
    // field, pre-populated with the module's current dimensions, and titled
    // "Change Tile Size".
    private async Task<NewModuleSpec?> PromptForTileSize(int currentW, int currentH)
    {
        var tcs = new TaskCompletionSource<NewModuleSpec?>();

        var widthBox  = new TextBox { Width = 70, Text = currentW.ToString(), FontSize = 12 };
        var heightBox = new TextBox { Width = 70, Text = currentH.ToString(), FontSize = 12 };
        var preview   = new TextBlock
        {
            Text = $"= {PixelsForTiles(currentW)} × {PixelsForTiles(currentH)} px",
            FontSize = 11,
            Foreground = Brushes.Gray,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };

        void UpdatePreview()
        {
            if (int.TryParse(widthBox.Text, out var w) && w >= 1 &&
                int.TryParse(heightBox.Text, out var h) && h >= 1)
            {
                preview.Text = $"= {PixelsForTiles(w)} × {PixelsForTiles(h)} px";
                preview.Foreground = Brushes.Gray;
            }
            else
            {
                preview.Text = "(width and height must be whole numbers ≥ 1)";
                preview.Foreground = new SolidColorBrush(Color.Parse("#D08080"));
            }
        }
        widthBox.TextChanged  += (_, _) => UpdatePreview();
        heightBox.TextChanged += (_, _) => UpdatePreview();

        var sizeRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = "Width",    FontSize = 12, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center },
                widthBox,
                new TextBlock { Text = "× Height", FontSize = 12, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center },
                heightBox,
                preview,
            },
        };
        var hint = new TextBlock
        {
            Text = "Module tile footprint. 1×1 ≈ 110×110 px; each extra tile adds 120 px.",
            FontSize = 11,
            Foreground = Brushes.Gray,
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };

        var okBtn = new Button { Content = "Apply", IsDefault = true, Padding = new Thickness(16, 4) };
        var cancel = new Button
        {
            Content = "Cancel",
            IsCancel = true,
            Padding = new Thickness(16, 4),
            Margin = new Thickness(8, 0, 0, 0),
        };

        var window = new Window
        {
            Title = "Change Tile Size",
            Width = 480,
            MinWidth = 380,
            MinHeight = 150,
            SizeToContent = SizeToContent.Height,
            CanResize = true,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 4,
                Children =
                {
                    new TextBlock { Text = "Tile size", Foreground = Brushes.White },
                    sizeRow,
                    hint,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Margin = new Thickness(0, 16, 0, 0),
                        Children = { okBtn, cancel },
                    },
                },
            },
        };
        StyleDialog(window);

        okBtn.Click += (_, _) =>
        {
            if (!int.TryParse(widthBox.Text,  out var w) || w < 1) { widthBox.Focus();  return; }
            if (!int.TryParse(heightBox.Text, out var h) || h < 1) { heightBox.Focus(); return; }
            tcs.TrySetResult(new NewModuleSpec(string.Empty, w, h));
            window.Close();
        };
        cancel.Click += (_, _) => { tcs.TrySetResult(null); window.Close(); };
        window.Closed += (_, _) => tcs.TrySetResult(null);

        widthBox.AttachedToVisualTree += (_, _) => { widthBox.Focus(); widthBox.SelectAll(); };

        await window.ShowDialog(this);
        return await tcs.Task;
    }
}

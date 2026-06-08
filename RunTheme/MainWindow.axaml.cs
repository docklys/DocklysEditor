using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace RunTheme;

public partial class MainWindow : Window
{
    private sealed record ThemeEntry(
        string Id,
        string Name,
        string FilePath,
        DateTime SavedAt,
        bool HasSettings,
        bool HasProfiles,
        Dictionary<string, string> Colors);

    private readonly List<ThemeEntry> _catalog = new();
    private int _currentIndex;

    private static readonly string ThemeLibraryDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Docklys", "ThemeLibrary");

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            LoadCatalog();
            ShowThemeAtIndex(0);
        };
    }

    // ── Catalog ───────────────────────────────────────────────────────────────

    private void LoadCatalog()
    {
        _catalog.Clear();
        if (!Directory.Exists(ThemeLibraryDir)) return;

        var indexPath = Path.Combine(ThemeLibraryDir, "index.json");
        if (!File.Exists(indexPath)) return;

        List<ThemeIndexEntry>? entries;
        try
        {
            entries = JsonSerializer.Deserialize<List<ThemeIndexEntry>>(
                File.ReadAllText(indexPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch { return; }

        if (entries == null) return;

        foreach (var e in entries.OrderByDescending(x => x.SavedAt))
        {
            var filePath = Path.Combine(ThemeLibraryDir, $"{e.Id}.dockly");
            if (!File.Exists(filePath)) continue;
            var colors = ReadColorsFromDockly(filePath);
            _catalog.Add(new ThemeEntry(e.Id, e.Name, filePath, e.SavedAt, e.HasSettings, e.HasProfiles, colors));
        }
    }

    private static Dictionary<string, string> ReadColorsFromDockly(string filePath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(filePath);
            var entry = zip.GetEntry("colors.json");
            if (entry == null) return new();
            using var reader = new StreamReader(entry.Open());
            return JsonSerializer.Deserialize<Dictionary<string, string>>(
                reader.ReadToEnd(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        }
        catch { return new(); }
    }

    private void ShowThemeAtIndex(int index)
    {
        var slot  = this.FindControl<ContentControl>("PreviewSlot")!;
        var label = this.FindControl<TextBlock>("ThemeNameLabel")!;

        if (_catalog.Count == 0)
        {
            slot.Content = BuildEmptyState();
            label.Text = "(no themes — save one from Docklys Settings)";
            return;
        }

        index = Math.Clamp(index, 0, _catalog.Count - 1);
        _currentIndex = index;
        var entry = _catalog[index];

        label.Text = $"{entry.Name}   ({index + 1}/{_catalog.Count})";
        slot.Content = BuildThemePreview(entry);
        RefreshMetaForCurrentTheme();
    }

    // ── Preview builder ───────────────────────────────────────────────────────

    private static Control BuildEmptyState()
    {
        return new TextBlock
        {
            Text = "No themes saved yet.\n\nIn Docklys, go to Settings → Theme Library and save your current look.",
            Foreground = Brushes.Gray,
            FontSize = 13,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 420,
        };
    }

    private static Control BuildThemePreview(ThemeEntry entry)
    {
        SolidColorBrush B(string key, string fallback)
        {
            if (entry.Colors.TryGetValue(key, out var hex) && Color.TryParse(hex, out var c))
                return new SolidColorBrush(c);
            return new SolidColorBrush(Color.Parse(fallback));
        }

        var bg         = B("ColorBackground",       "#2A2A2A");
        var bg2        = B("Color2Background",       "#111111");
        var moduleBg   = B("ColorModuleBackground",  "#1F1F1F");
        var moduleBdr  = B("ColorModuleBorder",      "#2F2F2F");
        var accent     = B("ColorAccent",            "#4F9CF9");
        var font       = B("ColorFont",              "#EDEDED");
        var moduleFont = B("ColorModuleFont",        "#FFFFFF");
        var windowBdr  = B("ColorWindowBorder",      "#000000");

        // Left panel: color swatches
        var swatches = new StackPanel { Spacing = 8, Width = 200 };
        void AddSwatch(string label, SolidColorBrush brush)
        {
            var hex = $"#{brush.Color.R:X2}{brush.Color.G:X2}{brush.Color.B:X2}";
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(new Border
            {
                Width = 28,
                Height = 18,
                Background = brush,
                BorderBrush = new SolidColorBrush(Colors.White) { Opacity = 0.25 },
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
            });
            row.Children.Add(new StackPanel
            {
                Spacing = 1,
                Children =
                {
                    new TextBlock { Text = label, FontSize = 10, Foreground = Brushes.LightGray },
                    new TextBlock { Text = hex, FontSize = 9, Foreground = Brushes.Gray, FontFamily = new FontFamily("Courier New") },
                }
            });
            swatches.Children.Add(row);
        }

        AddSwatch("Background",      bg);
        AddSwatch("Background 2",    bg2);
        AddSwatch("Module BG",       moduleBg);
        AddSwatch("Module Border",   moduleBdr);
        AddSwatch("Accent",          accent);
        AddSwatch("Font",            font);
        AddSwatch("Module Font",     moduleFont);
        AddSwatch("Window Border",   windowBdr);

        // Right panel: mini dock simulation
        var miniDock = BuildMiniDock(bg, bg2, moduleBg, moduleBdr, accent, font, moduleFont, windowBdr);

        // Metadata row
        var parts = new List<string>();
        if (entry.HasSettings) parts.Add("Settings");
        if (entry.HasProfiles) parts.Add("Profiles");
        var contentDesc = parts.Count > 0 ? string.Join(" · ", parts) : "Empty";

        var meta = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center };
        meta.Children.Add(new TextBlock
        {
            Text = entry.Name,
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        meta.Children.Add(new TextBlock
        {
            Text = entry.SavedAt.ToString("MMM d, yyyy  HH:mm") + "  ·  " + contentDesc,
            FontSize = 11,
            Foreground = Brushes.Gray,
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        var content = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 20,
            Margin = new Thickness(20),
        };

        var panels = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 24,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        panels.Children.Add(swatches);
        panels.Children.Add(miniDock);

        content.Children.Add(meta);
        content.Children.Add(panels);

        return new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };
    }

    private static Control BuildMiniDock(
        SolidColorBrush bg, SolidColorBrush bg2,
        SolidColorBrush moduleBg, SolidColorBrush moduleBdr,
        SolidColorBrush accent, SolidColorBrush font,
        SolidColorBrush moduleFont, SolidColorBrush windowBdr)
    {
        // Simulate the Docklys dock: a narrow vertical panel with modules
        Control MakeTile(string label)
        {
            var inner = new StackPanel { Spacing = 4, Margin = new Thickness(6) };
            inner.Children.Add(new TextBlock { Text = label, FontSize = 9, Foreground = moduleFont });
            inner.Children.Add(new Border { Height = 4, Background = accent, CornerRadius = new CornerRadius(2) });
            inner.Children.Add(new Border { Height = 4, Background = moduleBdr, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 20, 0) });
            inner.Children.Add(new Border { Height = 4, Background = moduleBdr, CornerRadius = new CornerRadius(2), Margin = new Thickness(10, 0, 0, 0) });
            return new Border
            {
                Background = moduleBg,
                BorderBrush = moduleBdr,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Child = inner,
                Width = 90,
                Height = 60,
                Margin = new Thickness(3),
            };
        }

        // Profile dots
        var profileRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4) };
        for (int i = 0; i < 3; i++)
            profileRow.Children.Add(new Border
            {
                Width = 8, Height = 8,
                Background = i == 0 ? accent : moduleBdr,
                CornerRadius = new CornerRadius(4),
            });

        // Module grid 2x2
        var grid = new WrapPanel { Width = 200 };
        grid.Children.Add(MakeTile("Clock"));
        grid.Children.Add(MakeTile("Music"));
        grid.Children.Add(MakeTile("System"));
        grid.Children.Add(MakeTile("Weather"));

        var dock = new StackPanel { Spacing = 6, Margin = new Thickness(8) };
        dock.Children.Add(profileRow);
        dock.Children.Add(grid);

        return new Border
        {
            Background = bg,
            BorderBrush = windowBdr,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Child = dock,
            Width = 216,
        };
    }

    // ── Carousel ──────────────────────────────────────────────────────────────

    private void OnPrevClick(object? sender, RoutedEventArgs e)
    {
        if (_catalog.Count == 0) return;
        ShowThemeAtIndex((_currentIndex - 1 + _catalog.Count) % _catalog.Count);
    }

    private void OnNextClick(object? sender, RoutedEventArgs e)
    {
        if (_catalog.Count == 0) return;
        ShowThemeAtIndex((_currentIndex + 1) % _catalog.Count);
    }

    private void OnReloadClick(object? sender, RoutedEventArgs e)
    {
        LoadCatalog();
        ShowThemeAtIndex(Math.Clamp(_currentIndex, 0, Math.Max(0, _catalog.Count - 1)));
    }

    // ── Apply to Docklys ─────────────────────────────────────────────────────

    private async void OnApplyClick(object? sender, RoutedEventArgs e)
    {
        if (_catalog.Count == 0) { await ShowMessageDialog("Apply Theme", "No theme selected."); return; }
        var entry = _catalog[_currentIndex];
        var btn = sender as Button;
        if (btn != null) btn.IsEnabled = false;

        try
        {
            var docklysData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Docklys");
            Directory.CreateDirectory(docklysData);

            using var zip = ZipFile.OpenRead(entry.FilePath);

            bool applied = false;
            var colorsEntry = zip.GetEntry("colors.json");
            if (colorsEntry != null)
            {
                colorsEntry.ExtractToFile(Path.Combine(docklysData, "colors.json"), overwrite: true);
                applied = true;
            }
            var settingsEntry = zip.GetEntry("settings.json");
            if (settingsEntry != null)
            {
                settingsEntry.ExtractToFile(Path.Combine(docklysData, "Settings.json"), overwrite: true);
                applied = true;
            }

            if (applied)
                await ShowMessageDialog("Theme Applied",
                    $"'{entry.Name}' has been applied.\n\nRestart Docklys (or use Settings → Import) to see the changes.");
            else
                await ShowMessageDialog("Nothing to Apply",
                    $"'{entry.Name}' doesn't contain colors or settings data.");
        }
        catch (Exception ex)
        {
            await ShowMessageDialog("Apply failed", $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (btn != null) btn.IsEnabled = true;
        }
    }

    // ── Folder buttons ────────────────────────────────────────────────────────

    private void OnOpenThemeFolderClick(object? sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(ThemeLibraryDir);
        OpenFolder(ThemeLibraryDir);
    }

    private void OpenLegalLink_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem && menuItem.Tag is string url)
        {
            try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); }
            catch (Exception ex) { Debug.WriteLine($"[Legal] Failed: {ex.Message}"); }
        }
    }

    private void OpenFolder(string dir)
    {
        try { Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true }); }
        catch (Exception ex) { _ = ShowMessageDialog("Open folder", $"Couldn't open:\n{dir}\n\n{ex.Message}"); }
    }

    private static string? FindEditorSolutionDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DefaultModule.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    // Minimal index entry model (mirrors ThemeLibraryEntry from Docklys).
    private sealed class ThemeIndexEntry
    {
        [JsonPropertyName("id")]          public string   Id          { get; set; } = "";
        [JsonPropertyName("name")]        public string   Name        { get; set; } = "";
        [JsonPropertyName("savedAt")]     public DateTime SavedAt     { get; set; }
        [JsonPropertyName("hasSettings")] public bool     HasSettings { get; set; }
        [JsonPropertyName("hasProfiles")] public bool     HasProfiles { get; set; }
        [JsonPropertyName("hasModules")]  public bool     HasModules  { get; set; }
    }
}

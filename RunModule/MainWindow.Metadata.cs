using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace RunModule;

public partial class MainWindow
{
    private sealed class DocklysMeta
    {
        [JsonPropertyName("name")]        public string Name        { get; set; } = "";
        [JsonPropertyName("type")]        public string Type        { get; set; } = "module";
        [JsonPropertyName("license")]     public string License     { get; set; } = "None";
        [JsonPropertyName("description")] public string Description { get; set; } = "";
        [JsonPropertyName("version")]     public string Version     { get; set; } = "1.0";
        [JsonPropertyName("sizeBytes")]   public long   SizeBytes   { get; set; }
        [JsonPropertyName("githubRepo")]  public string GithubRepo  { get; set; } = "";
    }

    // Called from ShowModuleAtIndex after a module entry is displayed.
    private void RefreshMetaForCurrentModule()
    {
        if (_catalog.Count == 0) return;
        var entry = _catalog[Math.Clamp(_currentIndex, 0, _catalog.Count - 1)];
        var folder = Path.GetDirectoryName(entry.CsprojPath)!;

        Directory.CreateDirectory(Path.Combine(folder, "Images"));

        var meta = ReadMeta(folder) ?? new DocklysMeta();
        meta.Name    = entry.FolderName;
        meta.Type    = "module";
        meta.License = DetectLicense(folder, meta.License);
        if (File.Exists(entry.DllPath)) meta.SizeBytes = new FileInfo(entry.DllPath).Length;
        WriteMeta(folder, meta);
        CopyMetaToOutput(folder, "OutputModuleDLL");

        // Fire-and-forget: fetch GitHub description if none set yet.
        _ = FetchGitHubDescriptionAsync(folder);
    }

    // ── JSON helpers ────────────────────────────────────────────────────────────

    private static DocklysMeta? ReadMeta(string folder)
    {
        var path = Path.Combine(folder, "docklys.json");
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<DocklysMeta>(File.ReadAllText(path)); }
        catch { return null; }
    }

    private static void WriteMeta(string folder, DocklysMeta meta)
    {
        File.WriteAllText(Path.Combine(folder, "docklys.json"),
            JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string DetectLicense(string folder, string current)
    {
        var lf = Path.Combine(folder, "LICENSE.txt");
        if (!File.Exists(lf)) return current;
        var first = File.ReadLines(lf).FirstOrDefault("") ?? "";
        if (first.Contains("MIT",    StringComparison.OrdinalIgnoreCase)) return "MIT";
        if (first.Contains("Apache", StringComparison.OrdinalIgnoreCase)) return "Apache 2.0";
        if (first.Contains("GPL",    StringComparison.OrdinalIgnoreCase)) return "GPLv3";
        return current;
    }

    private static void CopyMetaToOutput(string folder, string outputDirName)
    {
        var editorRoot = FindEditorSolutionDir();
        if (editorRoot == null) return;
        var outDir = Path.Combine(editorRoot, outputDirName);
        if (!Directory.Exists(outDir)) return;

        var metaSrc = Path.Combine(folder, "docklys.json");
        if (File.Exists(metaSrc))
            File.Copy(metaSrc, Path.Combine(outDir, "docklys.json"), overwrite: true);

        var imgSrc = Path.Combine(folder, "Images");
        if (!Directory.Exists(imgSrc)) return;
        var imgDst = Path.Combine(outDir, "Images");
        Directory.CreateDirectory(imgDst);
        foreach (var f in Directory.GetFiles(imgSrc)
                     .Where(f => IsImageExt(Path.GetExtension(f))))
            File.Copy(f, Path.Combine(imgDst, Path.GetFileName(f)), overwrite: true);
    }

    private static bool IsImageExt(string ext)
    {
        var e = ext.ToLowerInvariant();
        return e is ".webp" or ".png" or ".jpg" or ".jpeg";
    }

    // ── GitHub description ──────────────────────────────────────────────────────

    private static async Task FetchGitHubDescriptionAsync(string folder)
    {
        try
        {
            var (ok, remote) = await RunProcessAsync("git", "remote get-url origin", folder);
            if (!ok || string.IsNullOrWhiteSpace(remote)) return;
            var (owner, repo) = ParseGitHubUrl(remote.Trim());
            if (owner == null || repo == null) return;

            var gh = ResolveGhPath();
            var (descOk, desc) = await RunProcessAsync(
                gh, $"api repos/{owner}/{repo} --jq .description", AppContext.BaseDirectory);
            if (!descOk) return;
            desc = desc.Trim();
            if (string.IsNullOrEmpty(desc) || desc == "null") return;

            var meta = ReadMeta(folder) ?? new DocklysMeta { Name = Path.GetFileName(folder) };
            if (string.IsNullOrEmpty(meta.Description))
            {
                meta.Description = desc;
                meta.GithubRepo  = $"{owner}/{repo}";
                WriteMeta(folder, meta);
            }
        }
        catch { }
    }

    internal static (string? owner, string? repo) ParseGitHubUrl(string url)
    {
        string? rest = null;
        if (url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
            rest = url["https://github.com/".Length..];
        else if (url.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase))
            rest = url["git@github.com:".Length..];
        if (rest == null) return (null, null);
        rest = rest.TrimEnd('/');
        if (rest.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) rest = rest[..^4];
        var parts = rest.Split('/');
        return parts.Length >= 2 ? (parts[0], parts[1]) : (null, null);
    }

    // ── Info dialog ─────────────────────────────────────────────────────────────

    private async void OnInfoClick(object? sender, RoutedEventArgs e)
    {
        if (_catalog.Count == 0) { await ShowMessageDialog("Info", "No module loaded."); return; }
        var entry = _catalog[Math.Clamp(_currentIndex, 0, _catalog.Count - 1)];
        var folder = Path.GetDirectoryName(entry.CsprojPath)!;
        var meta   = ReadMeta(folder) ?? new DocklysMeta { Name = entry.FolderName, Type = "module" };
        await ShowInfoDialogAsync(folder, meta);
    }

    private async Task ShowInfoDialogAsync(string folder, DocklysMeta meta)
    {
        var descBox = new TextBox
        {
            Text        = meta.Description,
            Watermark   = "Describe your module for the marketplace…",
            AcceptsReturn = true,
            MinHeight   = 80,
            MaxHeight   = 180,
            TextWrapping = TextWrapping.Wrap,
        };

        var fields = new StackPanel { Spacing = 4 };

        void Row(string label, string value)
        {
            fields.Children.Add(new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("90,*"),
                Children =
                {
                    new TextBlock { Text = label, Foreground = Brushes.Gray, FontSize = 11,
                                    VerticalAlignment = VerticalAlignment.Center, [Grid.ColumnProperty] = 0 },
                    new TextBlock { Text = value, Foreground = Brushes.White, FontSize = 11,
                                    TextWrapping = TextWrapping.Wrap,
                                    VerticalAlignment = VerticalAlignment.Center, [Grid.ColumnProperty] = 1 },
                }
            });
        }
        Row("Name",    meta.Name);
        Row("Type",    meta.Type);
        Row("License", meta.License);
        Row("Size",    meta.SizeBytes > 0 ? $"{meta.SizeBytes / 1024.0:F1} KB" : "—");
        Row("GitHub",  string.IsNullOrEmpty(meta.GithubRepo) ? "(not linked yet)" : meta.GithubRepo);

        fields.Children.Add(new TextBlock
        {
            Text = "Description", Foreground = Brushes.Gray, FontSize = 11,
            Margin = new Thickness(0, 10, 0, 3)
        });
        fields.Children.Add(descBox);

        var syncBtn   = new Button { Content = "Sync from GitHub", Padding = new Thickness(10, 3) };
        var saveBtn   = new Button { Content = "Save", IsDefault = true, Padding = new Thickness(16, 4) };
        var cancelBtn = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(16, 4),
                                     Margin = new Thickness(8, 0, 0, 0) };

        var btnRow = new Grid
        {
            Margin = new Thickness(0, 12, 0, 0),
            ColumnDefinitions = new ColumnDefinitions("Auto, *, Auto, Auto"),
        };
        btnRow.Children.Add(syncBtn);
        Grid.SetColumn(saveBtn,   2);
        Grid.SetColumn(cancelBtn, 3);
        btnRow.Children.Add(saveBtn);
        btnRow.Children.Add(cancelBtn);

        var dp = new DockPanel { Margin = new Thickness(16), LastChildFill = true };
        DockPanel.SetDock(btnRow, Dock.Bottom);
        dp.Children.Add(btnRow);
        dp.Children.Add(new ScrollViewer
        {
            Content = fields,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });

        var w = new Window
        {
            Title  = $"Module Info — {meta.Name}",
            Width  = 460,
            Height = 380,
            MinHeight = 260,
            CanResize = true,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = dp,
        };
        StyleDialog(w);

        syncBtn.Click += async (_, _) =>
        {
            syncBtn.IsEnabled = false;
            syncBtn.Content   = "Syncing…";
            try
            {
                var (remOk, rem) = await RunProcessAsync("git", "remote get-url origin", folder);
                if (remOk && !string.IsNullOrWhiteSpace(rem))
                {
                    var (owner, repo) = ParseGitHubUrl(rem.Trim());
                    if (owner != null && repo != null)
                    {
                        var gh = ResolveGhPath();
                        var (dOk, d) = await RunProcessAsync(
                            gh, $"api repos/{owner}/{repo} --jq .description", AppContext.BaseDirectory);
                        if (dOk && !string.IsNullOrEmpty(d.Trim()) && d.Trim() != "null")
                        {
                            descBox.Text     = d.Trim();
                            meta.GithubRepo  = $"{owner}/{repo}";
                            syncBtn.Content  = "✓ Synced";
                            await Task.Delay(1200);
                        }
                        else { syncBtn.Content = "No description found"; await Task.Delay(1500); }
                    }
                    else { syncBtn.Content = "No GitHub remote"; await Task.Delay(1500); }
                }
                else { syncBtn.Content = "No remote found"; await Task.Delay(1500); }
            }
            finally { syncBtn.IsEnabled = true; syncBtn.Content = "Sync from GitHub"; }
        };

        saveBtn.Click += (_, _) =>
        {
            meta.Description = descBox.Text?.Trim() ?? "";
            WriteMeta(folder, meta);
            CopyMetaToOutput(folder, "OutputModuleDLL");
            w.Close();
        };
        cancelBtn.Click += (_, _) => w.Close();

        await w.ShowDialog(this);
    }

    // ── Images dialog ───────────────────────────────────────────────────────────

    private async void OnImagesClick(object? sender, RoutedEventArgs e)
    {
        if (_catalog.Count == 0) { await ShowMessageDialog("Images", "No module loaded."); return; }
        var entry = _catalog[Math.Clamp(_currentIndex, 0, _catalog.Count - 1)];
        var folder = Path.GetDirectoryName(entry.CsprojPath)!;
        var imagesDir = Path.Combine(folder, "Images");
        Directory.CreateDirectory(imagesDir);
        await ShowImagesDialogAsync(folder, imagesDir, entry.FolderName, "OutputModuleDLL");
    }

    private async Task ShowImagesDialogAsync(string folder, string imagesDir, string label, string outputDir)
    {
        Window? w = null;
        StackPanel? listPanel = null;

        void Refresh()
        {
            if (listPanel == null) return;
            listPanel.Children.Clear();

            var files = Directory.Exists(imagesDir)
                ? Directory.GetFiles(imagesDir)
                    .Where(f => IsImageExt(Path.GetExtension(f)))
                    .OrderBy(f => ImageSortKey(Path.GetFileName(f)))
                    .ToList()
                : new List<string>();

            if (files.Count == 0)
            {
                listPanel.Children.Add(new TextBlock
                {
                    Text = "No images yet. Use the buttons below to add images.",
                    Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap, FontSize = 11
                });
                return;
            }

            foreach (var f in files)
            {
                var fn       = Path.GetFileName(f);
                var friendly = ImageFriendlyName(fn);
                var color    = ImageLabelColor(fn);

                var removeBtn = new Button
                {
                    Content = "✕", FontSize = 9, Padding = new Thickness(4, 1),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                removeBtn.Click += (_, _) =>
                {
                    try { File.Delete(f); } catch { }
                    Refresh();
                };

                var row = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*, Auto"),
                    Margin = new Thickness(0, 2),
                };
                row.Children.Add(new TextBlock
                {
                    Text = friendly, Foreground = color, FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                Grid.SetColumn(removeBtn, 1);
                row.Children.Add(removeBtn);
                listPanel.Children.Add(row);
            }
        }

        // Pick a file and save it with a specific dest filename (without extension).
        async Task AddImageAs(string destBase)
        {
            var tl = TopLevel.GetTopLevel(this);
            if (tl == null) return;
            var picked = await tl.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title        = "Pick an image file",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Images")
                        { Patterns = new[] { "*.webp", "*.png", "*.jpg", "*.jpeg" } }
                }
            });
            var file = picked.FirstOrDefault();
            if (file == null) return;
            var srcPath = file.Path.LocalPath;
            var ext     = Path.GetExtension(srcPath).ToLowerInvariant();
            Directory.CreateDirectory(imagesDir);
            File.Copy(srcPath, Path.Combine(imagesDir, destBase + ext), overwrite: true);
            CopyMetaToOutput(folder, outputDir);
            Refresh();
        }

        async Task AddScreenshot()
        {
            int n = 1;
            while (Directory.GetFiles(imagesDir)
                       .Any(f => Path.GetFileNameWithoutExtension(f)
                                    .Equals($"screenshot-{n}", StringComparison.OrdinalIgnoreCase)))
                n++;
            await AddImageAs($"screenshot-{n}");
        }

        listPanel = new StackPanel { Spacing = 2 };
        Refresh();

        var hint = new TextBlock
        {
            Text = "icon.webp → marketplace icon    preview.webp → main screenshot    screenshot-N.webp → extra screenshots",
            FontSize = 10, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        };

        var setIconBtn   = new Button { Content = "Set Icon",       Padding = new Thickness(10, 3) };
        var setPreviewBtn = new Button { Content = "Set Preview",    Padding = new Thickness(10, 3) };
        var addSsBtn     = new Button { Content = "Add Screenshot", Padding = new Thickness(10, 3) };
        var fromGhBtn    = new Button { Content = "From GitHub",    Padding = new Thickness(10, 3) };
        var openDirBtn   = new Button { Content = "Open Folder",    Padding = new Thickness(10, 3) };
        var closeBtn     = new Button { Content = "Close", IsDefault = true, IsCancel = true,
                                        Padding = new Thickness(16, 4) };

        setIconBtn.Click    += async (_, _) => await AddImageAs("icon");
        setPreviewBtn.Click += async (_, _) => await AddImageAs("preview");
        addSsBtn.Click      += async (_, _) => await AddScreenshot();
        fromGhBtn.Click     += async (_, _) =>
        {
            fromGhBtn.IsEnabled = false;
            fromGhBtn.Content   = "Pulling…";
            await PullImagesFromGitHubAsync(folder, imagesDir);
            CopyMetaToOutput(folder, outputDir);
            Refresh();
            fromGhBtn.IsEnabled = true;
            fromGhBtn.Content   = "From GitHub";
        };
        openDirBtn.Click += (_, _) =>
        {
            Directory.CreateDirectory(imagesDir);
            Process.Start(new ProcessStartInfo { FileName = imagesDir, UseShellExecute = true });
        };
        closeBtn.Click += (_, _) => w?.Close();

        var actionRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin  = new Thickness(0, 8, 0, 0),
        };
        foreach (var b in new Button[] { setIconBtn, setPreviewBtn, addSsBtn, fromGhBtn, openDirBtn })
            actionRow.Children.Add(b);

        var bottomRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 6, 0, 0),
            Children = { closeBtn },
        };

        var dp = new DockPanel { Margin = new Thickness(14), LastChildFill = true };
        DockPanel.SetDock(bottomRow, Dock.Bottom);
        DockPanel.SetDock(actionRow, Dock.Bottom);
        dp.Children.Add(bottomRow);
        dp.Children.Add(actionRow);
        dp.Children.Add(new ScrollViewer
        {
            Content = new StackPanel { Spacing = 4, Children = { hint, listPanel } },
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MinHeight = 60,
        });

        w = new Window
        {
            Title  = $"Images — {label}",
            Width  = 500,
            Height = 310,
            MinHeight = 200,
            CanResize = true,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = dp,
        };
        StyleDialog(w);
        await w.ShowDialog(this);
    }

    private static async Task PullImagesFromGitHubAsync(string folder, string imagesDir)
    {
        try
        {
            var (ok, remote) = await RunProcessAsync("git", "remote get-url origin", folder);
            if (!ok || string.IsNullOrWhiteSpace(remote)) return;
            var (owner, repo) = ParseGitHubUrl(remote.Trim());
            if (owner == null || repo == null) return;

            var gh = ResolveGhPath();
            // List contents of the Images/ folder in the repo.
            var (listOk, listJson) = await RunProcessAsync(
                gh, $"api repos/{owner}/{repo}/contents/Images", AppContext.BaseDirectory);
            if (!listOk || string.IsNullOrWhiteSpace(listJson)) return;

            Directory.CreateDirectory(imagesDir);
            using var doc = JsonDocument.Parse(listJson);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (!item.TryGetProperty("name", out var nameProp)) continue;
                if (!item.TryGetProperty("type", out var typeProp)) continue;
                if (typeProp.GetString() != "file") continue;

                var name = nameProp.GetString();
                if (name == null) continue;
                var ext = Path.GetExtension(name).ToLowerInvariant();
                if (!IsImageExt(ext)) continue;

                // Download via gh API (base64 encoded content).
                var (dlOk, contentJson) = await RunProcessAsync(
                    gh, $"api repos/{owner}/{repo}/contents/Images/{name}", AppContext.BaseDirectory);
                if (!dlOk || string.IsNullOrWhiteSpace(contentJson)) continue;

                try
                {
                    using var cdoc = JsonDocument.Parse(contentJson);
                    var b64 = cdoc.RootElement.GetProperty("content").GetString() ?? "";
                    b64 = b64.Replace("\n", "").Replace("\\n", "").Trim();
                    var bytes = Convert.FromBase64String(b64);
                    File.WriteAllBytes(Path.Combine(imagesDir, name), bytes);
                }
                catch { }
            }
        }
        catch { }
    }

    // ── Label helpers ───────────────────────────────────────────────────────────

    private static string ImageFriendlyName(string filename) => filename.ToLowerInvariant() switch
    {
        "icon.webp" or "icon.png"       => "icon.webp  —  Marketplace icon",
        "preview.webp" or "preview.png" => "preview.webp  —  Main screenshot",
        _ when filename.StartsWith("screenshot-", StringComparison.OrdinalIgnoreCase)
            => $"{filename}  —  Extra screenshot",
        _ => filename,
    };

    private static IBrush ImageLabelColor(string filename) => filename.ToLowerInvariant() switch
    {
        "icon.webp" or "icon.png"       => Brushes.LightGreen,
        "preview.webp" or "preview.png" => Brushes.LightSkyBlue,
        _ => Brushes.White,
    };

    private static int ImageSortKey(string filename) => filename.ToLowerInvariant() switch
    {
        "icon.webp" or "icon.png"       => 0,
        "preview.webp" or "preview.png" => 1,
        _ => 2,
    };
}

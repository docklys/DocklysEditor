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

namespace RunTheme;

public partial class MainWindow
{
    // Sidecar metadata stored alongside the .dockly file.
    // Saved as <ThemeLibraryDir>/<id>.meta.json
    private sealed class ThemeMeta
    {
        [JsonPropertyName("name")]        public string Name        { get; set; } = "";
        [JsonPropertyName("type")]        public string Type        { get; set; } = "theme";
        [JsonPropertyName("license")]     public string License     { get; set; } = "None";
        [JsonPropertyName("description")] public string Description { get; set; } = "";
        [JsonPropertyName("version")]     public string Version     { get; set; } = "1.0";
        [JsonPropertyName("githubRepo")]  public string GithubRepo  { get; set; } = "";
    }

    private void RefreshMetaForCurrentTheme()
    {
        if (_catalog.Count == 0) return;
        var entry = _catalog[Math.Clamp(_currentIndex, 0, _catalog.Count - 1)];

        var meta  = ReadThemeMeta(entry.Id) ?? new ThemeMeta();
        meta.Name = entry.Name;
        meta.Type = "theme";
        WriteThemeMeta(entry.Id, meta);

        _ = FetchGitHubDescriptionAsync(entry.Id, meta.GithubRepo);
    }

    private static string MetaPath(string themeId) =>
        Path.Combine(ThemeLibraryDir, $"{themeId}.meta.json");

    private static ThemeMeta? ReadThemeMeta(string themeId)
    {
        var path = MetaPath(themeId);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<ThemeMeta>(File.ReadAllText(path)); }
        catch { return null; }
    }

    private static void WriteThemeMeta(string themeId, ThemeMeta meta)
    {
        File.WriteAllText(MetaPath(themeId),
            JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string ImagesDir(string themeId) =>
        Path.Combine(ThemeLibraryDir, "Images", themeId);

    // ── GitHub description ──────────────────────────────────────────────────────

    private static async Task FetchGitHubDescriptionAsync(string themeId, string knownRepo)
    {
        if (string.IsNullOrEmpty(knownRepo)) return;
        try
        {
            var (owner, repo) = ParseGitHubUrl(knownRepo);
            if (owner == null || repo == null) return;
            var gh = ResolveGhPath();
            var (ok, desc) = await RunProcessAsync(
                gh, $"api repos/{owner}/{repo} --jq .description", AppContext.BaseDirectory);
            if (!ok) return;
            desc = desc.Trim();
            if (string.IsNullOrEmpty(desc) || desc == "null") return;
            var meta = ReadThemeMeta(themeId) ?? new ThemeMeta { Name = themeId };
            if (string.IsNullOrEmpty(meta.Description))
            {
                meta.Description = desc;
                WriteThemeMeta(themeId, meta);
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

    // ── Publish ──────────────────────────────────────────────────────────────────

    private async void OnPublishClick(object? sender, RoutedEventArgs e)
    {
        if (_catalog.Count == 0) { await ShowMessageDialog("Publish", "No theme loaded."); return; }
        var entry = _catalog[Math.Clamp(_currentIndex, 0, _catalog.Count - 1)];
        var meta = ReadThemeMeta(entry.Id) ?? new ThemeMeta { Name = entry.Name };

        var btn = sender as Button;
        var images = new List<string>();
        if (btn != null) { btn.IsEnabled = false; btn.Content = "Checking…"; }
        try { images = await FetchRemoteImageUrlsAsync(meta.GithubRepo); }
        catch (Exception ex) { Debug.WriteLine($"[Publish] Image lookup failed: {ex.Message}"); }
        finally { if (btn != null) { btn.IsEnabled = true; btn.Content = "🌐 Publish"; } }

        var url = BuildPublishUrl("Theme", meta.Name is { Length: > 0 } n ? n : entry.Name,
            entry.Id, meta.Version, meta.Description, meta.License, meta.GithubRepo, images);

        try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); }
        catch (Exception ex) { Debug.WriteLine($"[Publish] Failed to open browser: {ex.Message}"); }
    }

    // The one platform we can vouch for is the one the editor is running on;
    // nothing here declares OS support, so anything else would be a guess the
    // developer would have to notice and correct.
    private static string CurrentPlatform() =>
        OperatingSystem.IsWindows() ? "Windows" :
        OperatingSystem.IsMacOS()   ? "macOS"   :
        OperatingSystem.IsLinux()   ? "Linux"   : "";

    // Only images GitHub confirms are pushed can be handed over: the browser
    // has no access to the local Images/ folder, and a URL built by hand for an
    // image that was never pushed would arrive as a broken thumbnail.
    private static async Task<List<string>> FetchRemoteImageUrlsAsync(string githubRepo)
    {
        var urls = new List<string>();
        if (string.IsNullOrWhiteSpace(githubRepo)) return urls;

        var (owner, repo) = ParseGitHubUrl(
            githubRepo.Contains("github.com", StringComparison.OrdinalIgnoreCase)
                ? githubRepo : $"https://github.com/{githubRepo}");
        if (owner == null || repo == null) return urls;

        // download_url comes straight from the API, so it carries the repo's real
        // default branch and is known-good rather than assembled from guesses.
        var (ok, json) = await RunProcessRawAsync(ResolveGhPath(),
            $"api repos/{owner}/{repo}/contents/Images", AppContext.BaseDirectory);
        if (!ok || string.IsNullOrWhiteSpace(json)) return urls;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return urls;

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (!item.TryGetProperty("type", out var t) || t.GetString() != "file") continue;
                if (!item.TryGetProperty("name", out var n)) continue;
                var fileName = n.GetString() ?? "";
                if (!fileName.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)) continue;
                if (!item.TryGetProperty("download_url", out var d)) continue;
                var dl = d.GetString();
                if (!string.IsNullOrWhiteSpace(dl)) urls.Add(dl!);
            }
        }
        catch (Exception ex) { Debug.WriteLine($"[Publish] Images listing parse failed: {ex.Message}"); }

        return urls;
    }

    // RunProcessAsync truncates its log to keep dialogs readable, which would
    // corrupt JSON. This variant returns stdout intact.
    private static async Task<(bool ok, string output)> RunProcessRawAsync(string exe, string args, string workDir)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                WorkingDirectory = workDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return (false, "");
            var stdout = await p.StandardOutput.ReadToEndAsync();
            await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            return (p.ExitCode == 0, stdout);
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    private static string BuildPublishUrl(string type, string name, string idSource, string version,
        string description, string license, string githubRepo, IReadOnlyList<string> imageUrls)
    {
        var q = new List<string>();
        void Add(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) q.Add($"{key}={Uri.EscapeDataString(value)}");
        }

        Add("pf_type", type);
        Add("pf_name", name);
        Add("pf_id", SlugifyId(idSource));
        Add("pf_version", version);
        Add("pf_description", description);
        Add("pf_license", license is "" or "None" ? null : license);
        Add("pf_github", string.IsNullOrWhiteSpace(githubRepo) ? null
            : githubRepo.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? githubRepo : $"https://github.com/{githubRepo}");
        Add("pf_platforms", CurrentPlatform());

        foreach (var img in imageUrls)
        {
            var file = Path.GetFileNameWithoutExtension(img);
            if (file.Equals("icon", StringComparison.OrdinalIgnoreCase)) Add("pf_icon", img);
            else Add("pf_shot", img);
        }

        return "https://docklys.com/DocklysEditor" + (q.Count > 0 ? "?" + string.Join("&", q) : "");
    }

    private static string SlugifyId(string name)
    {
        var slug = new string(name.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray());
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return slug.Trim('-');
    }

    private async void OnInfoClick(object? sender, RoutedEventArgs e)
    {
        if (_catalog.Count == 0) { await ShowMessageDialog("Info", "No theme loaded."); return; }
        var entry = _catalog[Math.Clamp(_currentIndex, 0, _catalog.Count - 1)];
        var meta  = ReadThemeMeta(entry.Id) ?? new ThemeMeta { Name = entry.Name };
        await ShowInfoDialogAsync(entry.Id, meta, entry.Colors.Count);
    }

    private async Task ShowInfoDialogAsync(string themeId, ThemeMeta meta, int colorsCount)
    {
        var descBox = new TextBox
        {
            Text = meta.Description, Watermark = "Describe your theme for the marketplace…",
            AcceptsReturn = true, MinHeight = 80, MaxHeight = 180, TextWrapping = TextWrapping.Wrap,
        };

        var licenseOptions = new[] { "None", "MIT", "Apache 2.0", "GPLv3", "CC BY 4.0", "CC BY-SA 4.0", "CC0 1.0" };
        var licenseBox = new ComboBox { ItemsSource = licenseOptions, Width = 200 };
        licenseBox.SelectedItem = licenseOptions.Contains(meta.License) ? meta.License : "None";

        var githubBox = new TextBox { Width = 300, Text = meta.GithubRepo, Watermark = "owner/repo or full URL" };

        var fields = new StackPanel { Spacing = 6 };
        void Row(string lbl, Control ctrl) => fields.Children.Add(new StackPanel { Spacing = 4, Children = {
            new TextBlock { Text = lbl, Foreground = Brushes.Gray, FontSize = 11 }, ctrl
        }});
        void InfoRow(string lbl, string val) => fields.Children.Add(new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("90,*"),
            Children = {
                new TextBlock { Text = lbl, Foreground = Brushes.Gray, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, [Grid.ColumnProperty] = 0 },
                new TextBlock { Text = val, Foreground = Brushes.White, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, [Grid.ColumnProperty] = 1 },
            }
        });
        InfoRow("Name",   meta.Name);
        InfoRow("Type",   "theme");
        InfoRow("Colors", $"{colorsCount} color entries");
        fields.Children.Add(new TextBlock { Text = "License", Foreground = Brushes.Gray, FontSize = 11, Margin = new Thickness(0, 6, 0, 2) });
        fields.Children.Add(licenseBox);
        fields.Children.Add(new TextBlock { Text = "GitHub Repo", Foreground = Brushes.Gray, FontSize = 11, Margin = new Thickness(0, 6, 0, 2) });
        fields.Children.Add(githubBox);
        fields.Children.Add(new TextBlock { Text = "Description", Foreground = Brushes.Gray, FontSize = 11, Margin = new Thickness(0, 6, 0, 2) });
        fields.Children.Add(descBox);

        var syncBtn   = new Button { Content = "Sync from GitHub", Padding = new Thickness(10, 3) };
        var saveBtn   = new Button { Content = "Save", IsDefault = true, Padding = new Thickness(16, 4) };
        var cancelBtn = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(16, 4), Margin = new Thickness(8, 0, 0, 0) };

        var btnRow = new Grid { Margin = new Thickness(0, 12, 0, 0), ColumnDefinitions = new ColumnDefinitions("Auto, *, Auto, Auto") };
        btnRow.Children.Add(syncBtn);
        Grid.SetColumn(saveBtn, 2); Grid.SetColumn(cancelBtn, 3);
        btnRow.Children.Add(saveBtn); btnRow.Children.Add(cancelBtn);

        var dp = new DockPanel { Margin = new Thickness(16), LastChildFill = true };
        DockPanel.SetDock(btnRow, Dock.Bottom);
        dp.Children.Add(btnRow);
        dp.Children.Add(new ScrollViewer { Content = fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        var w = new Window { Title = $"Theme Info — {meta.Name}", Width = 460, Height = 440, MinHeight = 300, CanResize = true, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = dp };
        StyleDialog(w);

        syncBtn.Click += async (_, _) =>
        {
            syncBtn.IsEnabled = false; syncBtn.Content = "Syncing…";
            try
            {
                var repoInput = githubBox.Text?.Trim() ?? "";
                var (owner, repo) = ParseGitHubUrl(repoInput.Contains("github.com") ? repoInput : $"https://github.com/{repoInput}");
                if (owner != null && repo != null)
                {
                    var gh = ResolveGhPath();
                    var (dOk, d) = await RunProcessAsync(gh, $"api repos/{owner}/{repo} --jq .description", AppContext.BaseDirectory);
                    if (dOk && !string.IsNullOrEmpty(d.Trim()) && d.Trim() != "null")
                    { descBox.Text = d.Trim(); meta.GithubRepo = $"{owner}/{repo}"; syncBtn.Content = "✓ Synced"; await Task.Delay(1200); }
                    else { syncBtn.Content = "No description found"; await Task.Delay(1500); }
                }
                else { syncBtn.Content = "Invalid repo format"; await Task.Delay(1500); }
            }
            finally { syncBtn.IsEnabled = true; syncBtn.Content = "Sync from GitHub"; }
        };
        saveBtn.Click += (_, _) =>
        {
            meta.Description = descBox.Text?.Trim() ?? "";
            meta.License     = licenseBox.SelectedItem?.ToString() ?? "None";
            meta.GithubRepo  = githubBox.Text?.Trim() ?? "";
            WriteThemeMeta(themeId, meta);
            w.Close();
        };
        cancelBtn.Click += (_, _) => w.Close();
        await w.ShowDialog(this);
    }

    // ── Images dialog ───────────────────────────────────────────────────────────

    private async void OnImagesClick(object? sender, RoutedEventArgs e)
    {
        if (_catalog.Count == 0) { await ShowMessageDialog("Images", "No theme loaded."); return; }
        var entry     = _catalog[Math.Clamp(_currentIndex, 0, _catalog.Count - 1)];
        var imagesDir = ImagesDir(entry.Id);
        Directory.CreateDirectory(imagesDir);
        await ShowImagesDialogAsync(imagesDir, entry.Name, entry.Id);
    }

    private async Task ShowImagesDialogAsync(string imagesDir, string label, string themeId)
    {
        Window? w = null;
        StackPanel? listPanel = null;

        void Refresh()
        {
            if (listPanel == null) return;
            listPanel.Children.Clear();

            // Show Images/ folder contents + any preview WebPs in the theme dir root.
            var files = new List<string>();
            var iconCandidate  = Path.Combine(ThemeLibraryDir, $"{themeId}-icon.webp");
            var prevCandidate  = Path.Combine(ThemeLibraryDir, $"{themeId}-preview.webp");
            if (File.Exists(iconCandidate))  files.Add(iconCandidate);
            if (File.Exists(prevCandidate))  files.Add(prevCandidate);
            if (Directory.Exists(imagesDir))
                files.AddRange(Directory.GetFiles(imagesDir).Where(f => IsImageExt(Path.GetExtension(f))).OrderBy(Path.GetFileName));

            if (files.Count == 0)
            {
                listPanel.Children.Add(new TextBlock { Text = "No images yet. Use the buttons below to add images.", Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap, FontSize = 11 });
                return;
            }
            foreach (var f in files.Distinct())
            {
                var fn  = Path.GetFileName(f);
                var rmv = new Button { Content = "✕", FontSize = 9, Padding = new Thickness(4, 1), VerticalAlignment = VerticalAlignment.Center };
                rmv.Click += (_, _) => { try { File.Delete(f); } catch { } Refresh(); };
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*, Auto"), Margin = new Thickness(0, 2) };
                row.Children.Add(new TextBlock { Text = FriendlyThemeName(fn, themeId), Foreground = LabelColor(fn, themeId), FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
                Grid.SetColumn(rmv, 1); row.Children.Add(rmv);
                listPanel.Children.Add(row);
            }
        }

        async Task AddAs(string destPath)
        {
            var tl = TopLevel.GetTopLevel(this);
            if (tl == null) return;
            var picked = await tl.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Pick an image", AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType("Images") { Patterns = new[] { "*.webp", "*.png", "*.jpg", "*.jpeg" } } }
            });
            var file = picked.FirstOrDefault();
            if (file == null) return;
            var ext = Path.GetExtension(file.Path.LocalPath).ToLowerInvariant();
            var finalDest = Path.ChangeExtension(destPath, ext);
            Directory.CreateDirectory(Path.GetDirectoryName(finalDest)!);
            File.Copy(file.Path.LocalPath, finalDest, overwrite: true);
            Refresh();
        }

        async Task AddScreenshot()
        {
            Directory.CreateDirectory(imagesDir);
            int n = 1;
            while (Directory.GetFiles(imagesDir).Any(f => Path.GetFileNameWithoutExtension(f).Equals($"screenshot-{n}", StringComparison.OrdinalIgnoreCase))) n++;
            await AddAs(Path.Combine(imagesDir, $"screenshot-{n}.webp"));
        }

        listPanel = new StackPanel { Spacing = 2 }; Refresh();
        var hint = new TextBlock { Text = "icon → marketplace icon    preview → main screenshot    screenshot-N → extra screenshots", FontSize = 10, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };

        var setIconBtn    = new Button { Content = "Set Icon",       Padding = new Thickness(10, 3) };
        var setPreviewBtn = new Button { Content = "Set Preview",    Padding = new Thickness(10, 3) };
        var addSsBtn      = new Button { Content = "Add Screenshot", Padding = new Thickness(10, 3) };
        var openDirBtn    = new Button { Content = "Open Folder",    Padding = new Thickness(10, 3) };
        var closeBtn      = new Button { Content = "Close", IsDefault = true, IsCancel = true, Padding = new Thickness(16, 4) };

        setIconBtn.Click    += async (_, _) => await AddAs(Path.Combine(ThemeLibraryDir, $"{themeId}-icon.webp"));
        setPreviewBtn.Click += async (_, _) => await AddAs(Path.Combine(ThemeLibraryDir, $"{themeId}-preview.webp"));
        addSsBtn.Click      += async (_, _) => await AddScreenshot();
        openDirBtn.Click    += (_, _) => { Directory.CreateDirectory(imagesDir); Process.Start(new ProcessStartInfo { FileName = imagesDir, UseShellExecute = true }); };
        closeBtn.Click      += (_, _) => w?.Close();

        var actionRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 8, 0, 0) };
        foreach (var b in new[] { setIconBtn, setPreviewBtn, addSsBtn, openDirBtn }) actionRow.Children.Add(b);
        var bottomRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0), Children = { closeBtn } };

        var dp = new DockPanel { Margin = new Thickness(14), LastChildFill = true };
        DockPanel.SetDock(bottomRow, Dock.Bottom); DockPanel.SetDock(actionRow, Dock.Bottom);
        dp.Children.Add(bottomRow); dp.Children.Add(actionRow);
        dp.Children.Add(new ScrollViewer { Content = new StackPanel { Spacing = 4, Children = { hint, listPanel } }, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 60 });

        w = new Window { Title = $"Images — {label}", Width = 500, Height = 310, MinHeight = 200, CanResize = true, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = dp };
        StyleDialog(w);
        await w.ShowDialog(this);
    }

    private static bool IsImageExt(string ext)
    {
        var e = ext.ToLowerInvariant();
        return e is ".webp" or ".png" or ".jpg" or ".jpeg";
    }

    private static string FriendlyThemeName(string fn, string themeId)
    {
        var lower = fn.ToLowerInvariant();
        if (lower == $"{themeId.ToLowerInvariant()}-icon.webp") return $"{fn}  —  Marketplace icon";
        if (lower == $"{themeId.ToLowerInvariant()}-preview.webp") return $"{fn}  —  Main screenshot";
        if (lower.StartsWith("screenshot-")) return $"{fn}  —  Extra screenshot";
        return fn;
    }

    private static IBrush LabelColor(string fn, string themeId)
    {
        var lower = fn.ToLowerInvariant();
        if (lower == $"{themeId.ToLowerInvariant()}-icon.webp") return Brushes.LightGreen;
        if (lower == $"{themeId.ToLowerInvariant()}-preview.webp") return Brushes.LightSkyBlue;
        return Brushes.White;
    }
}

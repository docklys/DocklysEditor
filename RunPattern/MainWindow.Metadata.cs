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

namespace RunPattern;

public partial class MainWindow
{
    private sealed class DocklysMeta
    {
        [JsonPropertyName("name")]        public string Name        { get; set; } = "";
        [JsonPropertyName("type")]        public string Type        { get; set; } = "pattern";
        [JsonPropertyName("license")]     public string License     { get; set; } = "None";
        [JsonPropertyName("description")] public string Description { get; set; } = "";
        [JsonPropertyName("version")]     public string Version     { get; set; } = "1.0";
        [JsonPropertyName("sizeBytes")]   public long   SizeBytes   { get; set; }
        [JsonPropertyName("githubRepo")]  public string GithubRepo  { get; set; } = "";
    }

    private void RefreshMetaForCurrentPattern()
    {
        if (_catalog.Count == 0) return;
        var entry  = _catalog[Math.Clamp(_currentIndex, 0, _catalog.Count - 1)];
        var folder = Path.GetDirectoryName(entry.CsprojPath)!;

        Directory.CreateDirectory(Path.Combine(folder, "Images"));

        var meta   = ReadMeta(folder) ?? new DocklysMeta();
        meta.Name  = entry.FolderName;
        meta.Type  = "pattern";
        meta.License = DetectLicense(folder, meta.License);
        if (File.Exists(entry.DllPath)) meta.SizeBytes = new FileInfo(entry.DllPath).Length;
        WriteMeta(folder, meta);
        CopyMetaToOutput(folder);

        _ = FetchGitHubDescriptionAsync(folder);
    }

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
        // GenerateLicenseFile writes the GPL's real title, which never contains
        // the literal "GPL" — matching only that let a GPLv3 pick decay to None.
        if (first.Contains("GPL",             StringComparison.OrdinalIgnoreCase) ||
            first.Contains("GENERAL PUBLIC",  StringComparison.OrdinalIgnoreCase)) return "GPLv3";
        return current;
    }

    private static void CopyMetaToOutput(string folder)
    {
        var editorRoot = FindEditorSolutionDir();
        if (editorRoot == null) return;
        var outDir = Path.Combine(editorRoot, "OutputPatternDLL");
        if (!Directory.Exists(outDir)) return;

        var metaSrc = Path.Combine(folder, "docklys.json");
        if (File.Exists(metaSrc))
            File.Copy(metaSrc, Path.Combine(outDir, "docklys.json"), overwrite: true);

        var imgSrc = Path.Combine(folder, "Images");
        if (!Directory.Exists(imgSrc)) return;
        var imgDst = Path.Combine(outDir, "Images");
        Directory.CreateDirectory(imgDst);
        foreach (var f in Directory.GetFiles(imgSrc).Where(f => IsImageExt(Path.GetExtension(f))))
            File.Copy(f, Path.Combine(imgDst, Path.GetFileName(f)), overwrite: true);
    }

    private static bool IsImageExt(string ext)
    {
        var e = ext.ToLowerInvariant();
        return e is ".webp" or ".png" or ".jpg" or ".jpeg";
    }

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

    // ── Publish ──────────────────────────────────────────────────────────────────

    private async void OnPublishClick(object? sender, RoutedEventArgs e)
    {
        if (_catalog.Count == 0) { await ShowMessageDialog("Publish", "No pattern loaded."); return; }
        var entry = _catalog[Math.Clamp(_currentIndex, 0, _catalog.Count - 1)];
        var folder = Path.GetDirectoryName(entry.CsprojPath)!;
        var meta = ReadMeta(folder) ?? new DocklysMeta { Name = entry.FolderName, Type = "pattern" };

        var btn = sender as Button;
        var images = new List<string>();
        if (btn != null) { btn.IsEnabled = false; btn.Content = "Checking…"; }
        try { images = await FetchRemoteImageUrlsAsync(meta.GithubRepo); }
        catch (Exception ex) { Debug.WriteLine($"[Publish] Image lookup failed: {ex.Message}"); }
        finally { if (btn != null) { btn.IsEnabled = true; btn.Content = "🌐 Publish"; } }

        var url = BuildPublishUrl("Pattern", meta.Name is { Length: > 0 } n ? n : entry.FolderName,
            entry.FolderName, meta.Version, meta.Description, meta.License, meta.GithubRepo, images);

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
        if (_catalog.Count == 0) { await ShowMessageDialog("Info", "No pattern loaded."); return; }
        var entry  = _catalog[Math.Clamp(_currentIndex, 0, _catalog.Count - 1)];
        var folder = Path.GetDirectoryName(entry.CsprojPath)!;
        var meta   = ReadMeta(folder) ?? new DocklysMeta { Name = entry.FolderName, Type = "pattern" };
        await ShowInfoDialogAsync(folder, meta);
    }

    private async Task ShowInfoDialogAsync(string folder, DocklysMeta meta)
    {
        var descBox = new TextBox
        {
            Text = meta.Description, Watermark = "Describe your pattern for the marketplace…",
            AcceptsReturn = true, MinHeight = 80, MaxHeight = 180, TextWrapping = TextWrapping.Wrap,
        };

        var fields = new StackPanel { Spacing = 4 };
        void Row(string lbl, string val) => fields.Children.Add(new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("90,*"),
            Children =
            {
                new TextBlock { Text = lbl, Foreground = Brushes.Gray, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, [Grid.ColumnProperty] = 0 },
                new TextBlock { Text = val, Foreground = Brushes.White, FontSize = 11, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, [Grid.ColumnProperty] = 1 },
            }
        });
        Row("Name",    meta.Name);
        Row("Type",    meta.Type);
        Row("License", meta.License);
        Row("Size",    meta.SizeBytes > 0 ? $"{meta.SizeBytes / 1024.0:F1} KB" : "—");
        Row("GitHub",  string.IsNullOrEmpty(meta.GithubRepo) ? "(not linked yet)" : meta.GithubRepo);
        fields.Children.Add(new TextBlock { Text = "Description", Foreground = Brushes.Gray, FontSize = 11, Margin = new Thickness(0, 10, 0, 3) });
        fields.Children.Add(descBox);

        var syncBtn   = new Button { Content = "Sync from GitHub", Padding = new Thickness(10, 3) };
        var saveBtn   = new Button { Content = "Save", IsDefault = true, Padding = new Thickness(16, 4) };
        var cancelBtn = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(16, 4), Margin = new Thickness(8, 0, 0, 0) };

        var btnRow = new Grid { Margin = new Thickness(0, 12, 0, 0), ColumnDefinitions = new ColumnDefinitions("Auto, *, Auto, Auto") };
        btnRow.Children.Add(syncBtn);
        Grid.SetColumn(saveBtn,   2); Grid.SetColumn(cancelBtn, 3);
        btnRow.Children.Add(saveBtn); btnRow.Children.Add(cancelBtn);

        var dp = new DockPanel { Margin = new Thickness(16), LastChildFill = true };
        DockPanel.SetDock(btnRow, Dock.Bottom);
        dp.Children.Add(btnRow);
        dp.Children.Add(new ScrollViewer { Content = fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        var w = new Window { Title = $"Pattern Info — {meta.Name}", Width = 460, Height = 380, MinHeight = 260, CanResize = true, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = dp };
        StyleDialog(w);

        syncBtn.Click += async (_, _) =>
        {
            syncBtn.IsEnabled = false; syncBtn.Content = "Syncing…";
            try
            {
                var (remOk, rem) = await RunProcessAsync("git", "remote get-url origin", folder);
                if (remOk && !string.IsNullOrWhiteSpace(rem))
                {
                    var (owner, repo) = ParseGitHubUrl(rem.Trim());
                    if (owner != null && repo != null)
                    {
                        var gh = ResolveGhPath();
                        var (dOk, d) = await RunProcessAsync(gh, $"api repos/{owner}/{repo} --jq .description", AppContext.BaseDirectory);
                        if (dOk && !string.IsNullOrEmpty(d.Trim()) && d.Trim() != "null")
                        { descBox.Text = d.Trim(); meta.GithubRepo = $"{owner}/{repo}"; syncBtn.Content = "✓ Synced"; await Task.Delay(1200); }
                        else { syncBtn.Content = "No description found"; await Task.Delay(1500); }
                    }
                    else { syncBtn.Content = "No GitHub remote"; await Task.Delay(1500); }
                }
                else { syncBtn.Content = "No remote found"; await Task.Delay(1500); }
            }
            finally { syncBtn.IsEnabled = true; syncBtn.Content = "Sync from GitHub"; }
        };
        saveBtn.Click   += (_, _) => { meta.Description = descBox.Text?.Trim() ?? ""; WriteMeta(folder, meta); CopyMetaToOutput(folder); w.Close(); };
        cancelBtn.Click += (_, _) => w.Close();
        await w.ShowDialog(this);
    }

    private async void OnImagesClick(object? sender, RoutedEventArgs e)
    {
        if (_catalog.Count == 0) { await ShowMessageDialog("Images", "No pattern loaded."); return; }
        var entry     = _catalog[Math.Clamp(_currentIndex, 0, _catalog.Count - 1)];
        var folder    = Path.GetDirectoryName(entry.CsprojPath)!;
        var imagesDir = Path.Combine(folder, "Images");
        Directory.CreateDirectory(imagesDir);
        await ShowImagesDialogAsync(folder, imagesDir, entry.FolderName);
    }

    private async Task ShowImagesDialogAsync(string folder, string imagesDir, string label)
    {
        Window? w = null;
        StackPanel? listPanel = null;

        void Refresh()
        {
            if (listPanel == null) return;
            listPanel.Children.Clear();
            var files = Directory.Exists(imagesDir)
                ? Directory.GetFiles(imagesDir).Where(f => IsImageExt(Path.GetExtension(f)))
                    .OrderBy(f => SortKey(Path.GetFileName(f))).ToList()
                : new List<string>();
            if (files.Count == 0) { listPanel.Children.Add(new TextBlock { Text = "No images yet.", Foreground = Brushes.Gray, FontSize = 11 }); return; }
            foreach (var f in files)
            {
                var fn  = Path.GetFileName(f);
                var rmv = new Button { Content = "✕", FontSize = 9, Padding = new Thickness(4, 1), VerticalAlignment = VerticalAlignment.Center };
                rmv.Click += (_, _) => { try { File.Delete(f); } catch { } Refresh(); };
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*, Auto"), Margin = new Thickness(0, 2) };
                row.Children.Add(new TextBlock { Text = FriendlyName(fn), Foreground = LabelColor(fn), FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
                Grid.SetColumn(rmv, 1); row.Children.Add(rmv);
                listPanel.Children.Add(row);
            }
        }

        async Task AddAs(string destBase)
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
            Directory.CreateDirectory(imagesDir);
            File.Copy(file.Path.LocalPath, Path.Combine(imagesDir, destBase + ext), overwrite: true);
            CopyMetaToOutput(folder); Refresh();
        }

        async Task AddScreenshot()
        {
            int n = 1;
            while (Directory.GetFiles(imagesDir).Any(f => Path.GetFileNameWithoutExtension(f).Equals($"screenshot-{n}", StringComparison.OrdinalIgnoreCase))) n++;
            await AddAs($"screenshot-{n}");
        }

        listPanel = new StackPanel { Spacing = 2 }; Refresh();
        var hint = new TextBlock { Text = "icon.webp → marketplace icon    preview.webp → main screenshot    screenshot-N.webp → extra", FontSize = 10, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };

        var setIconBtn    = new Button { Content = "Set Icon",       Padding = new Thickness(10, 3) };
        var setPreviewBtn = new Button { Content = "Set Preview",    Padding = new Thickness(10, 3) };
        var addSsBtn      = new Button { Content = "Add Screenshot", Padding = new Thickness(10, 3) };
        var fromGhBtn     = new Button { Content = "From GitHub",    Padding = new Thickness(10, 3) };
        var openDirBtn    = new Button { Content = "Open Folder",    Padding = new Thickness(10, 3) };
        var closeBtn      = new Button { Content = "Close", IsDefault = true, IsCancel = true, Padding = new Thickness(16, 4) };

        setIconBtn.Click    += async (_, _) => await AddAs("icon");
        setPreviewBtn.Click += async (_, _) => await AddAs("preview");
        addSsBtn.Click      += async (_, _) => await AddScreenshot();
        fromGhBtn.Click     += async (_, _) =>
        {
            fromGhBtn.IsEnabled = false; fromGhBtn.Content = "Pulling…";
            await PullImagesFromGitHubAsync(folder, imagesDir);
            CopyMetaToOutput(folder); Refresh();
            fromGhBtn.IsEnabled = true; fromGhBtn.Content = "From GitHub";
        };
        openDirBtn.Click += (_, _) => { Directory.CreateDirectory(imagesDir); Process.Start(new ProcessStartInfo { FileName = imagesDir, UseShellExecute = true }); };
        closeBtn.Click   += (_, _) => w?.Close();

        var actionRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 8, 0, 0) };
        foreach (var b in new[] { setIconBtn, setPreviewBtn, addSsBtn, fromGhBtn, openDirBtn }) actionRow.Children.Add(b);
        var bottomRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0), Children = { closeBtn } };

        var dp = new DockPanel { Margin = new Thickness(14), LastChildFill = true };
        DockPanel.SetDock(bottomRow, Dock.Bottom); DockPanel.SetDock(actionRow, Dock.Bottom);
        dp.Children.Add(bottomRow); dp.Children.Add(actionRow);
        dp.Children.Add(new ScrollViewer { Content = new StackPanel { Spacing = 4, Children = { hint, listPanel } }, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 60 });

        w = new Window { Title = $"Images — {label}", Width = 500, Height = 310, MinHeight = 200, CanResize = true, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = dp };
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
            var (listOk, listJson) = await RunProcessAsync(gh, $"api repos/{owner}/{repo}/contents/Images", AppContext.BaseDirectory);
            if (!listOk || string.IsNullOrWhiteSpace(listJson)) return;
            Directory.CreateDirectory(imagesDir);
            using var doc = JsonDocument.Parse(listJson);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (!item.TryGetProperty("name", out var np) || !item.TryGetProperty("type", out var tp)) continue;
                if (tp.GetString() != "file") continue;
                var name = np.GetString(); if (name == null) continue;
                if (!IsImageExt(Path.GetExtension(name))) continue;
                var (dlOk, cj) = await RunProcessAsync(gh, $"api repos/{owner}/{repo}/contents/Images/{name}", AppContext.BaseDirectory);
                if (!dlOk || string.IsNullOrWhiteSpace(cj)) continue;
                try
                {
                    using var cd = JsonDocument.Parse(cj);
                    var b64 = (cd.RootElement.GetProperty("content").GetString() ?? "").Replace("\n", "").Replace("\\n", "").Trim();
                    File.WriteAllBytes(Path.Combine(imagesDir, name), Convert.FromBase64String(b64));
                }
                catch { }
            }
        }
        catch { }
    }

    private static string FriendlyName(string fn) => fn.ToLowerInvariant() switch
    {
        "icon.webp" or "icon.png"       => "icon.webp  —  Marketplace icon",
        "preview.webp" or "preview.png" => "preview.webp  —  Main screenshot",
        _ when fn.StartsWith("screenshot-", StringComparison.OrdinalIgnoreCase) => $"{fn}  —  Extra screenshot",
        _ => fn,
    };

    private static IBrush LabelColor(string fn) => fn.ToLowerInvariant() switch
    {
        "icon.webp" or "icon.png"       => Brushes.LightGreen,
        "preview.webp" or "preview.png" => Brushes.LightGray,
        _ => Brushes.White,
    };

    private static int SortKey(string fn) => fn.ToLowerInvariant() switch
    {
        "icon.webp" or "icon.png"       => 0,
        "preview.webp" or "preview.png" => 1,
        _ => 2,
    };
}

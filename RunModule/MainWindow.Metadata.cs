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

    private sealed class DocklysManifest
    {
        [JsonPropertyName("schema_version")] public int SchemaVersion { get; set; } = 1;
        [JsonPropertyName("module_id")] public string ModuleId { get; set; } = "";
        [JsonPropertyName("version")] public string Version { get; set; } = "1.0.0";
        [JsonPropertyName("security_tier")] public int SecurityTier { get; set; }
        [JsonPropertyName("requested_capabilities")] public List<string> RequestedCapabilities { get; set; } = new();
    }

    private const string UiRenderCapability = "ui.render";
    private const string ModuleStorageReadCapability = "storage.module.read";
    private const string ModuleStorageWriteCapability = "storage.module.write";

    // New modules start with the safe, practical baseline: rendering plus access
    // to their own per-instance settings. Authors can reduce or extend it later
    // from Project → Permissions.
    private static readonly string[] TemplateCapabilities =
    {
        UiRenderCapability,
        ModuleStorageReadCapability,
        ModuleStorageWriteCapability,
    };

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

    private static DocklysManifest ReadManifest(string folder, string moduleId)
    {
        var path = Path.Combine(folder, "docklys.manifest.json");
        try
        {
            if (File.Exists(path))
            {
                var manifest = JsonSerializer.Deserialize<DocklysManifest>(File.ReadAllText(path));
                if (manifest != null)
                {
                    manifest.ModuleId = string.IsNullOrWhiteSpace(manifest.ModuleId) ? moduleId : manifest.ModuleId;
                    manifest.RequestedCapabilities ??= new List<string>();
                    return manifest;
                }
            }
        }
        catch { }

        return new DocklysManifest
        {
            ModuleId = moduleId,
            RequestedCapabilities = new List<string> { UiRenderCapability },
        };
    }

    private static void WriteManifest(string folder, DocklysManifest manifest)
    {
        manifest.SchemaVersion = 1;
        manifest.ModuleId = manifest.ModuleId.Trim();
        manifest.Version = string.IsNullOrWhiteSpace(manifest.Version) ? "1.0.0" : manifest.Version.Trim();
        manifest.RequestedCapabilities = manifest.RequestedCapabilities
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim())
            .Append(UiRenderCapability)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();

        // Module storage is privileged relative to rendering, so a manifest
        // requesting it can never be saved with an insufficient tier.
        if (manifest.RequestedCapabilities.Contains(ModuleStorageReadCapability, StringComparer.Ordinal) ||
            manifest.RequestedCapabilities.Contains(ModuleStorageWriteCapability, StringComparer.Ordinal))
            manifest.SecurityTier = Math.Max(manifest.SecurityTier, 1);
        manifest.SecurityTier = Math.Max(0, manifest.SecurityTier);

        File.WriteAllText(Path.Combine(folder, "docklys.manifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
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

    private static void CopyMetaToOutput(string folder, string outputDirName)
    {
        var editorRoot = FindEditorSolutionDir();
        if (editorRoot == null) return;
        var outDir = Path.Combine(editorRoot, outputDirName);
        if (!Directory.Exists(outDir)) return;

        var metaSrc = Path.Combine(folder, "docklys.json");
        if (File.Exists(metaSrc))
            File.Copy(metaSrc, Path.Combine(outDir, "docklys.json"), overwrite: true);

        var manifestSrc = Path.Combine(folder, "docklys.manifest.json");
        if (File.Exists(manifestSrc))
            File.Copy(manifestSrc, Path.Combine(outDir, "docklys.manifest.json"), overwrite: true);

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

    private async void OnPermissionsClick(object? sender, RoutedEventArgs e)
    {
        if (_catalog.Count == 0)
        {
            await ShowMessageDialog("Permissions", "No module is loaded.");
            return;
        }

        var entry = _catalog[Math.Clamp(_currentIndex, 0, _catalog.Count - 1)];
        var folder = Path.GetDirectoryName(entry.CsprojPath)!;
        var manifest = ReadManifest(folder, entry.FolderName);
        manifest.ModuleId = entry.FolderName;

        var requested = manifest.RequestedCapabilities.ToHashSet(StringComparer.Ordinal);
        var render = new CheckBox { Content = "ui.render — render the module UI", IsChecked = true, IsEnabled = false };
        var storageRead = new CheckBox { Content = "storage.module.read — read this module's saved settings", IsChecked = requested.Contains(ModuleStorageReadCapability) };
        var storageWrite = new CheckBox { Content = "storage.module.write — save this module's settings", IsChecked = requested.Contains(ModuleStorageWriteCapability) };
        var extra = new TextBox
        {
            Width = 440,
            MinHeight = 58,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Watermark = "One additional capability per line",
            Text = string.Join(Environment.NewLine, requested.Except(new[]
            {
                UiRenderCapability, ModuleStorageReadCapability, ModuleStorageWriteCapability
            }, StringComparer.Ordinal)),
        };
        var version = new TextBox
        {
            Width = 180,
            Text = manifest.Version,
        };
        var tier = new TextBox
        {
            Width = 120,
            Text = manifest.SecurityTier.ToString(),
        };
        var save = new Button { Content = "Save permissions", IsDefault = true, Padding = new Thickness(16, 4) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(16, 4) };
        Window? dialog = null;

        save.Click += (_, _) =>
        {
            if (!int.TryParse(tier.Text, out var selectedTier) || selectedTier < 0)
            {
                tier.Focus();
                return;
            }
            var capabilities = extra.Text?
                .Split(new[] { '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList() ?? new List<string>();
            capabilities.Add(UiRenderCapability);
            if (storageRead.IsChecked == true) capabilities.Add(ModuleStorageReadCapability);
            if (storageWrite.IsChecked == true) capabilities.Add(ModuleStorageWriteCapability);

            manifest.Version = version.Text ?? "";
            manifest.SecurityTier = selectedTier;
            manifest.RequestedCapabilities = capabilities;
            WriteManifest(folder, manifest);
            CopyMetaToOutput(folder, "OutputModuleDLL");
            dialog?.Close();
        };
        cancel.Click += (_, _) => dialog?.Close();

        dialog = new Window
        {
            Title = "Module permissions",
            Width = 520,
            MinWidth = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = true,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = $"{entry.FolderName} manifest", FontSize = 16, FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = "Request only the capabilities this module needs. Storage permissions require security tier 1.", FontSize = 11, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = $"Module ID: {entry.FolderName}", FontSize = 11, Foreground = Brushes.Gray },
                    new TextBlock { Text = "Manifest version", Margin = new Thickness(0, 8, 0, 0) },
                    version,
                    render,
                    storageRead,
                    storageWrite,
                    new TextBlock { Text = "Security tier", Margin = new Thickness(0, 8, 0, 0) },
                    tier,
                    new TextBlock { Text = "Additional capabilities", Margin = new Thickness(0, 8, 0, 0) },
                    extra,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Margin = new Thickness(0, 8, 0, 0),
                        Children = { cancel, save },
                    },
                },
            },
        };
        StyleDialog(dialog);
        await dialog.ShowDialog(this);
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

    // ── Publish ──────────────────────────────────────────────────────────────────

    private async void OnPublishClick(object? sender, RoutedEventArgs e)
    {
        if (_catalog.Count == 0) { await ShowMessageDialog("Publish", "No module loaded."); return; }
        var entry = _catalog[Math.Clamp(_currentIndex, 0, _catalog.Count - 1)];
        var folder = Path.GetDirectoryName(entry.CsprojPath)!;
        var meta = ReadMeta(folder) ?? new DocklysMeta { Name = entry.FolderName, Type = "module" };
        var (tileW, tileH) = ReadCurrentTileSize(folder);

        // Asking GitHub which images exist is a network round-trip.
        var btn = sender as Button;
        var images = new List<string>();
        if (btn != null) { btn.IsEnabled = false; btn.Content = "Checking…"; }
        try { images = await FetchRemoteImageUrlsAsync(meta.GithubRepo); }
        catch (Exception ex) { Debug.WriteLine($"[Publish] Image lookup failed: {ex.Message}"); }
        finally { if (btn != null) { btn.IsEnabled = true; btn.Content = "🌐 Publish"; } }

        var url = BuildPublishUrl("Module", meta.Name is { Length: > 0 } n ? n : entry.FolderName,
            entry.FolderName, meta.Version, meta.Description, meta.License, meta.GithubRepo,
            tileW, tileH, images);

        try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); }
        catch (Exception ex) { Debug.WriteLine($"[Publish] Failed to open browser: {ex.Message}"); }
    }

    // The one platform we can vouch for is the one the editor is running on;
    // nothing in the module declares its OS support, so anything else would be
    // a guess the developer would have to notice and correct.
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
        string description, string license, string githubRepo, int tileWidth, int tileHeight,
        IReadOnlyList<string> imageUrls)
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
        Add("pf_tile_w", tileWidth  > 0 ? tileWidth.ToString()  : null);
        Add("pf_tile_h", tileHeight > 0 ? tileHeight.ToString() : null);
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

    // ── License switcher ────────────────────────────────────────────────────────

    // The license is otherwise fixed at creation time: it is only ever read back
    // out of LICENSE.txt, so without this there is no way to change your mind.
    private async void OnLicenseClick(object? sender, RoutedEventArgs e)
    {
        if (_catalog.Count == 0) { await ShowMessageDialog("License", "No module loaded."); return; }
        var entry  = _catalog[Math.Clamp(_currentIndex, 0, _catalog.Count - 1)];
        var folder = Path.GetDirectoryName(entry.CsprojPath)!;
        var meta   = ReadMeta(folder) ?? new DocklysMeta { Name = entry.FolderName, Type = "module" };

        var options    = new[] { "None", "MIT", "Apache 2.0", "GPLv3" };
        var licenseBox = new ComboBox { ItemsSource = options, Width = 320 };
        licenseBox.SelectedItem = options.Contains(meta.License) ? meta.License : "None";

        var ok     = new Button { Content = "Save", IsDefault = true, Padding = new Thickness(16, 4) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(16, 4),
                                  Margin = new Thickness(8, 0, 0, 0) };

        var w = new Window
        {
            Title = $"License — {meta.Name}",
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 6,
                Children =
                {
                    new TextBlock { Text = "License", Foreground = Brushes.White },
                    licenseBox,
                    new TextBlock
                    {
                        Text = "Rewrites LICENSE.txt and updates docklys.json. Choosing None removes LICENSE.txt.",
                        Foreground = Brushes.Gray, FontSize = 11, TextWrapping = TextWrapping.Wrap,
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Margin = new Thickness(0, 12, 0, 0),
                        Children = { ok, cancel },
                    },
                },
            },
        };
        StyleDialog(w);

        ok.Click += (_, _) =>
        {
            var picked = licenseBox.SelectedItem?.ToString() ?? "None";
            try
            {
                var licensePath = Path.Combine(folder, "LICENSE.txt");
                if (picked == "None") { if (File.Exists(licensePath)) File.Delete(licensePath); }
                else GenerateLicenseFile(folder, picked);

                meta.License = picked;
                WriteMeta(folder, meta);
                CopyMetaToOutput(folder, "OutputModuleDLL");
            }
            catch (Exception ex) { Debug.WriteLine($"[License] Failed to apply {picked}: {ex.Message}"); }
            w.Close();
        };
        cancel.Click += (_, _) => w.Close();

        await w.ShowDialog(this);
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
        "preview.webp" or "preview.png" => Brushes.LightGray,
        _ => Brushes.White,
    };

    private static int ImageSortKey(string filename) => filename.ToLowerInvariant() switch
    {
        "icon.webp" or "icon.png"       => 0,
        "preview.webp" or "preview.png" => 1,
        _ => 2,
    };
}

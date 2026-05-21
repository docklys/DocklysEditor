using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace RunModule;

// "Create Module" — clones the DefaultModule source folder into a sibling
// folder named after the user's input, renames the .csproj/.axaml/.axaml.cs
// to match, and find-and-replaces the namespace/class/identifier strings
// inside the copied files. Also registers the new project with the
// solution and RunModule.csproj so the next rebuild picks it up.
public partial class MainWindow
{
    private static readonly Regex IdentifierRegex =
        new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    // Folder names we never overwrite — keep RunModule and the contracts
    // safe even if a user types one of them by mistake.
    private static readonly string[] ReservedModuleNames =
        { "DefaultModule", "RunModule", "Docklys.ModuleContracts", "VolumeMixer" };

    // Skipped while copying the template tree: VCS metadata, IDE caches,
    // build output, and the dotnet-new template config (we want a working
    // project, not another template).
    private static readonly string[] SkipDirectoryNames =
        { ".git", ".idea", ".vs", "bin", "obj", ".template.config" };

    // Files we copy byte-for-byte instead of treating as text. Anything
    // not in this list is read as UTF-8 and find-and-replaced.
    private static readonly string[] BinaryExtensions =
        { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ttf", ".otf",
          ".dll", ".pdb", ".exe", ".ico" };

    // Pixel footprint per tile unit. Matches the existing
    // .template.config switch table: 1 → 110, 2 → 230, … each tile is
    // 120px and tile-1 is 10px short.
    private static int PixelsForTiles(int tiles) => Math.Max(10, 120 * tiles - 10);

    private async void CreateModule_Click(object? sender, RoutedEventArgs e)
    {
        var solutionDir = FindEditorSolutionDir();
        if (solutionDir == null)
        {
            await ShowMessageDialog("Create Module",
                "Could not locate the DocklysModuleEditor solution root " +
                "(no DefaultModule.sln found by walking up from " +
                $"{AppContext.BaseDirectory}).");
            return;
        }

        var spec = await PromptForNewModuleSpec();
        if (spec == null) return;
        var name = spec.Name;

        var (ok, reason) = ValidateModuleName(name, solutionDir);
        if (!ok)
        {
            await ShowMessageDialog("Invalid name", reason!);
            return;
        }

        string targetDir = Path.Combine(solutionDir, name);
        try
        {
            CloneDefaultModuleInto(solutionDir, targetDir, name, spec.TileWidth, spec.TileHeight);
        }
        catch (Exception ex)
        {
            // Best-effort rollback so a half-cloned folder doesn't poison
            // the next attempt with the same name.
            TryDeleteDirectory(targetDir);
            await ShowMessageDialog("Scaffold failed",
                $"Failed to clone DefaultModule:\n\n{ex.GetType().Name}: {ex.Message}");
            return;
        }

        var slnPath = Path.Combine(solutionDir, "DefaultModule.sln");
        var newProj = Path.Combine(targetDir, name + ".csproj");
        var slnNote = await TryAddToSolution(slnPath, newProj);

        // Build the new project so a fresh DLL exists for catalog
        // discovery. Without this the next ReloadCatalog would skip the
        // new folder (no DLL = nothing to load).
        var buildNote = await TryBuildProject(newProj);

        // Refresh the catalog and jump straight to the newly-created module.
        // This is what makes "newly created modules work instantly" — the
        // user sees their module on screen immediately, no app restart.
        ReloadCatalogAndSelect(name);

        var pxW = PixelsForTiles(spec.TileWidth);
        var pxH = PixelsForTiles(spec.TileHeight);
        var msg = $"Module '{name}' created at:\n{targetDir}\n\n" +
                  $"Tile footprint: {spec.TileWidth}×{spec.TileHeight} ({pxW}×{pxH} px)";
        if (slnNote != null) msg += $"\n\nSolution note: {slnNote}";
        if (buildNote != null) msg += $"\n\nBuild note: {buildNote}\n" +
                                      "The module wasn't built, so it won't appear in the carousel yet. " +
                                      "Fix the build issue and click ↺ Reload Module to refresh.";

        await ShowMessageDialog("Module created", msg);
    }

    private sealed record NewModuleSpec(string Name, int TileWidth, int TileHeight);

    // Walk up from the running editor's BaseDirectory looking for the
    // editor solution. Mirrors the existing FindVolumeMixerProject /
    // FindDocklyCustomModulesDir patterns.
    private static string? FindEditorSolutionDir()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "DefaultModule.sln"))) return dir;
            var parent = Directory.GetParent(dir);
            if (parent == null) break;
            dir = parent.FullName;
        }
        return null;
    }

    // Local-registry validation only: collisions against sibling project
    // folders and against any DLL already deployed to Dockly's
    // CustomModules. The server-side registry at
    // registry.docklys.qwqc.dedyn.io is checked separately at submission
    // time.
    //
    // `allowExistingFolder` is for the rename flow: a case-only rename
    // (hello → Hello) needs to skip the collision check against itself.
    private static (bool ok, string? reason) ValidateModuleName(
        string name,
        string solutionDir,
        string? allowExistingFolder = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            return (false, "Module name cannot be empty.");
        if (!IdentifierRegex.IsMatch(name))
            return (false,
                "Module name must be a valid C# identifier — start with a letter " +
                "or underscore, then letters/digits/underscores. No spaces or special " +
                "characters.");

        if (ReservedModuleNames.Any(r => string.Equals(r, name, StringComparison.OrdinalIgnoreCase)))
            return (false, $"'{name}' is reserved by the editor solution. Pick a different name.");

        foreach (var folder in Directory.EnumerateDirectories(solutionDir))
        {
            var folderName = Path.GetFileName(folder);
            if (allowExistingFolder != null
                && string.Equals(folderName, allowExistingFolder, StringComparison.OrdinalIgnoreCase))
                continue;
            // Case-insensitive — Windows filesystems treat MyMod and mymod as
            // the same folder, and shipping a project that only builds on
            // case-sensitive filesystems would be a nasty surprise.
            if (string.Equals(folderName, name, StringComparison.OrdinalIgnoreCase))
                return (false,
                    $"A folder named '{folderName}' already exists in the solution. " +
                    "Pick a different name.");
        }

        var customModules = FindDocklyCustomModulesDirStatic();
        if (customModules != null)
        {
            var dllPath = Path.Combine(customModules, name + ".dll");
            if (File.Exists(dllPath))
                return (false,
                    $"Dockly already has a deployed module DLL named '{name}.dll' in:\n" +
                    $"{customModules}\n\nPick a different name, or remove the existing DLL first.");
        }

        return (true, null);
    }

    private static string? FindDocklyCustomModulesDirStatic()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            foreach (var rel in new[]
                     {
                         Path.Combine("Dockly", "CustomModules"),
                         Path.Combine("Dockly", "Dockly", "CustomModules"),
                     })
            {
                var candidate = Path.Combine(dir, rel);
                if (Directory.Exists(candidate)) return candidate;
            }
            var parent = Directory.GetParent(dir);
            if (parent == null) break;
            dir = parent.FullName;
        }
        return null;
    }

    private static void CloneDefaultModuleInto(
        string solutionDir,
        string targetDir,
        string newName,
        int tileWidth,
        int tileHeight)
    {
        var sourceDir = Path.Combine(solutionDir, "DefaultModule");
        if (!Directory.Exists(sourceDir))
            throw new DirectoryNotFoundException(
                $"DefaultModule template folder not found at: {sourceDir}");
        if (Directory.Exists(targetDir))
            throw new IOException($"Target folder already exists: {targetDir}");

        Directory.CreateDirectory(targetDir);
        CopyDirectoryFiltered(sourceDir, targetDir, newName);

        // Rename DefaultModule.* files (csproj, axaml, axaml.cs, …) to
        // <NewName>.* — the text inside already had the identifier rewritten
        // by CopyDirectoryFiltered.
        foreach (var pattern in new[] { "*.csproj", "*.axaml", "*.axaml.cs", "*.cs" })
        {
            foreach (var file in Directory.GetFiles(targetDir, pattern, SearchOption.AllDirectories))
            {
                var baseName = Path.GetFileName(file);
                if (baseName.StartsWith("DefaultModule", StringComparison.Ordinal))
                {
                    var newBase = newName + baseName.Substring("DefaultModule".Length);
                    var newPath = Path.Combine(Path.GetDirectoryName(file)!, newBase);
                    File.Move(file, newPath);
                }
            }
        }

        // Patch the tile size. Template defaults are 1×1 (Width/Height=110,
        // TileWidth/Height=>1); rewrite to the dev's chosen footprint.
        var pixelWidth = PixelsForTiles(tileWidth);
        var pixelHeight = PixelsForTiles(tileHeight);
        foreach (var file in Directory.GetFiles(targetDir, "*", SearchOption.AllDirectories))
        {
            if (IsBinary(file)) continue;
            try
            {
                var text = File.ReadAllText(file);
                var rewritten = text
                    .Replace("Width=\"110\"", $"Width=\"{pixelWidth}\"")
                    .Replace("Height=\"110\"", $"Height=\"{pixelHeight}\"")
                    .Replace("TileWidth => 1;", $"TileWidth => {tileWidth};")
                    .Replace("TileHeight => 1;", $"TileHeight => {tileHeight};");
                if (rewritten != text) File.WriteAllText(file, rewritten);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CreateModule] size rewrite failed for {file}: {ex.Message}");
            }
        }
    }

    private static void CopyDirectoryFiltered(string src, string dst, string newName)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(src))
        {
            var name = Path.GetFileName(entry);
            if (SkipDirectoryNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                continue;

            var targetPath = Path.Combine(dst, name);
            if (Directory.Exists(entry))
            {
                Directory.CreateDirectory(targetPath);
                CopyDirectoryFiltered(entry, targetPath, newName);
            }
            else
            {
                if (IsBinary(entry))
                {
                    File.Copy(entry, targetPath, overwrite: true);
                }
                else
                {
                    var text = File.ReadAllText(entry);
                    // Internal identifier swap. The template uses DefaultModule
                    // as the namespace + class + AssemblyName, BlackModule as
                    // the IModule.Id placeholder, and "Default Module" as the
                    // user-facing display name. Replacing all three gives a
                    // self-consistent scaffold the user can run immediately.
                    text = text.Replace("DefaultModule", newName);
                    text = text.Replace("BlackModule", newName);
                    text = text.Replace("\"Default Module\"", "\"" + newName + "\"");
                    File.WriteAllText(targetPath, text);
                }
            }
        }
    }

    private static bool IsBinary(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return BinaryExtensions.Contains(ext);
    }

    private static void TryDeleteDirectory(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception ex) { Debug.WriteLine($"[CreateModule] rollback delete failed: {ex.Message}"); }
    }

    private static async Task<string?> TryAddToSolution(string slnPath, string projPath)
    {
        if (!File.Exists(slnPath)) return $"Solution file not found at {slnPath}.";
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"sln \"{slnPath}\" add \"{projPath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(slnPath)!,
            };
            using var p = Process.Start(psi);
            if (p == null) return "Could not launch the dotnet CLI.";
            var stderr = await p.StandardError.ReadToEndAsync();
            var stdout = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            if (p.ExitCode != 0)
            {
                var msg = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                return $"`dotnet sln add` exited with {p.ExitCode}: {msg.Trim()}";
            }
            return null;
        }
        catch (Exception ex)
        {
            return $"`dotnet sln add` threw: {ex.GetType().Name}: {ex.Message}";
        }
    }

    // ── tiny modal dialogs ──────────────────────────────────────────────

    // Apply the same dark monochrome theme to any programmatic dialog
    // Window: ThemeVariant=Dark for built-in control templates, a black
    // background, and an accent palette swapped to neutral grays so
    // Fluent's blue/teal focus rings and selection accents come out
    // matching the main toolbar.
    internal static void StyleDialog(Window w)
    {
        w.RequestedThemeVariant = ThemeVariant.Dark;
        w.Background = new SolidColorBrush(Color.Parse("#1A1A1A"));
        w.Foreground = Brushes.White;

        w.Resources["SystemAccentColor"] = Color.Parse("#888888");
        w.Resources["SystemAccentColorLight1"] = Color.Parse("#9A9A9A");
        w.Resources["SystemAccentColorLight2"] = Color.Parse("#ABABAB");
        w.Resources["SystemAccentColorLight3"] = Color.Parse("#BCBCBC");
        w.Resources["SystemAccentColorDark1"] = Color.Parse("#777777");
        w.Resources["SystemAccentColorDark2"] = Color.Parse("#666666");
        w.Resources["SystemAccentColorDark3"] = Color.Parse("#555555");
    }
    //
    // Built inline rather than using MessageBox.Avalonia's input API
    // because that package's input-prompt class name moved between v2 and
    // v3, and we want this to work against whatever is referenced today.

    // Richer Create dialog: name + tile width + tile height. No upper limit
    // on the tile dimensions — devs can scaffold a 50×50 module if they
    // want one (host placement is on them). Live "X × Y px" preview
    // updates as they type so they can see the resulting AXAML footprint.
    private async Task<NewModuleSpec?> PromptForNewModuleSpec()
    {
        var tcs = new TaskCompletionSource<NewModuleSpec?>();

        var nameBox = new TextBox { Width = 320, Watermark = "e.g. MyNewModule" };
        var widthBox = new TextBox { Width = 70, Text = "1", FontSize = 12 };
        var heightBox = new TextBox { Width = 70, Text = "1", FontSize = 12 };
        var preview = new TextBlock
        {
            Text = "= 110 × 110 px",
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
        widthBox.TextChanged += (_, _) => UpdatePreview();
        heightBox.TextChanged += (_, _) => UpdatePreview();

        var nameHint = new TextBlock
        {
            Text = "Letters, digits, and underscores. Must start with a letter or underscore.",
            FontSize = 11,
            Foreground = Brushes.Gray,
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };

        var sizeRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = "Width", FontSize = 12, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center },
                widthBox,
                new TextBlock { Text = "× Height", FontSize = 12, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center },
                heightBox,
                preview,
            },
        };
        var sizeHint = new TextBlock
        {
            Text = "Module tile footprint. 1×1 ≈ 110×110 px; each extra tile adds 120 px. No upper limit.",
            FontSize = 11,
            Foreground = Brushes.Gray,
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };

        var okBtn = new Button { Content = "Create", IsDefault = true, Padding = new Thickness(16, 4) };
        var cancel = new Button
        {
            Content = "Cancel",
            IsCancel = true,
            Padding = new Thickness(16, 4),
            Margin = new Thickness(8, 0, 0, 0),
        };

        var window = new Window
        {
            Title = "Create New Module",
            Width = 480,
            MinWidth = 380,
            MinHeight = 180,
            SizeToContent = SizeToContent.Height,
            CanResize = true,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 4,
                Children =
                {
                    new TextBlock { Text = "Module name", Foreground = Brushes.White },
                    nameBox,
                    nameHint,
                    new TextBlock { Text = "Tile size", Foreground = Brushes.White, Margin = new Thickness(0, 12, 0, 0) },
                    sizeRow,
                    sizeHint,
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
            var rawName = nameBox.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(rawName)) { nameBox.Focus(); return; }
            if (!int.TryParse(widthBox.Text, out var w) || w < 1) { widthBox.Focus(); return; }
            if (!int.TryParse(heightBox.Text, out var h) || h < 1) { heightBox.Focus(); return; }
            tcs.TrySetResult(new NewModuleSpec(rawName, w, h));
            window.Close();
        };
        cancel.Click += (_, _) =>
        {
            tcs.TrySetResult(null);
            window.Close();
        };
        window.Closed += (_, _) => tcs.TrySetResult(null);

        nameBox.AttachedToVisualTree += (_, _) => nameBox.Focus();

        await window.ShowDialog(this);
        return await tcs.Task;
    }

    private async Task<string?> PromptForModuleName(string? initial = null, string title = "Create New Module")
    {
        var tcs = new TaskCompletionSource<string?>();

        var textbox = new TextBox
        {
            Width = 320,
            Watermark = "e.g. MyNewModule",
            Text = initial ?? "",
        };
        var hint = new TextBlock
        {
            Text = "Letters, digits, and underscores. Must start with a letter or underscore.",
            FontSize = 11,
            Foreground = Brushes.Gray,
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };
        var okBtn = new Button
        {
            Content = initial != null ? "Rename" : "Create",
            IsDefault = true,
            Padding = new Thickness(16, 4),
        };
        var cancel = new Button
        {
            Content = "Cancel",
            IsCancel = true,
            Padding = new Thickness(16, 4),
            Margin = new Thickness(8, 0, 0, 0),
        };

        var window = new Window
        {
            Title = title,
            Width = 400,
            MinWidth = 320,
            MinHeight = 160,
            SizeToContent = SizeToContent.Height,
            CanResize = true,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 4,
                Children =
                {
                    new TextBlock { Text = "Module name", Foreground = Brushes.White },
                    textbox,
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
            tcs.TrySetResult(textbox.Text);
            window.Close();
        };
        cancel.Click += (_, _) =>
        {
            tcs.TrySetResult(null);
            window.Close();
        };
        window.Closed += (_, _) => tcs.TrySetResult(null);

        textbox.AttachedToVisualTree += (_, _) =>
        {
            textbox.Focus();
            if (!string.IsNullOrEmpty(textbox.Text)) textbox.SelectAll();
        };

        await window.ShowDialog(this);
        return await tcs.Task;
    }

    private async Task ShowMessageDialog(string title, string message)
    {
        var tcs = new TaskCompletionSource<bool>();

        var copyBtn = new Button
        {
            Content = "📋 Copy",
            Padding = new Thickness(14, 4),
        };
        var ok = new Button
        {
            Content = "OK",
            IsDefault = true,
            IsCancel = true,
            Padding = new Thickness(20, 4),
            Margin = new Thickness(8, 0, 0, 0),
        };

        // Buttons: Copy on the left, OK on the right — both in one row.
        var buttonRow = new Grid
        {
            Margin = new Thickness(0, 12, 0, 0),
            ColumnDefinitions = new ColumnDefinitions("Auto, *, Auto"),
        };
        Grid.SetColumn(copyBtn, 0);
        Grid.SetColumn(ok, 2);
        buttonRow.Children.Add(copyBtn);
        buttonRow.Children.Add(ok);

        // Scrollable text area so the window can be shrunk and long messages
        // don't require a very tall window.
        var scrollViewer = new ScrollViewer
        {
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.White,
            },
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };

        // DockPanel: buttons pinned to bottom, text fills the rest so
        // resizing the window gives more/less visible text.
        var dockPanel = new DockPanel { Margin = new Thickness(16), LastChildFill = true };
        DockPanel.SetDock(buttonRow, Dock.Bottom);
        dockPanel.Children.Add(buttonRow);
        dockPanel.Children.Add(scrollViewer);

        var window = new Window
        {
            Title = title,
            Width = 460,
            MinWidth = 340,
            MinHeight = 140,
            MaxHeight = 620,
            SizeToContent = SizeToContent.Height,
            CanResize = true,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = dockPanel,
        };
        StyleDialog(window);

        copyBtn.Click += async (_, _) =>
        {
            var clipboard = TopLevel.GetTopLevel(window)?.Clipboard;
            if (clipboard != null) await clipboard.SetTextAsync(message);
            copyBtn.Content = "✓ Copied";
            await Task.Delay(1400);
            if (window.IsVisible) copyBtn.Content = "📋 Copy";
        };
        ok.Click += (_, _) => { tcs.TrySetResult(true); window.Close(); };
        window.Closed += (_, _) => tcs.TrySetResult(true);

        await window.ShowDialog(this);
        await tcs.Task;
    }
}

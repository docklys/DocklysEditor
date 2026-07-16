using System;
using System.Collections.Generic;
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

        // Step 1: pick which existing module to use as a template.
        var templateName = await PromptForTemplateName(solutionDir);
        if (templateName == null) return;

        // Step 2: pick name + tile size for the new module.
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
            CloneModuleInto(solutionDir, targetDir, name, spec.TileWidth, spec.TileHeight, templateName);
            GenerateLicenseFile(targetDir, spec.License);
        }
        catch (Exception ex)
        {
            TryDeleteDirectory(targetDir);
            await ShowMessageDialog("Scaffold failed",
                $"Failed to clone '{templateName}':\n\n{ex.GetType().Name}: {ex.Message}");
            return;
        }

        var slnPath = Path.Combine(solutionDir, "DefaultModule.sln");
        var newProj = Path.Combine(targetDir, name + ".csproj");
        var slnNote = await TryAddToSolution(slnPath, newProj);

        var buildNote = await TryBuildProject(newProj);

        ReloadCatalogAndSelect(name);

        var pxW = PixelsForTiles(spec.TileWidth);
        var pxH = PixelsForTiles(spec.TileHeight);
        var msg = $"Module '{name}' created from template '{templateName}' at:\n{targetDir}\n\n" +
                  $"Tile footprint: {spec.TileWidth}×{spec.TileHeight} ({pxW}×{pxH} px)";
        if (slnNote != null) msg += $"\n\nSolution note: {slnNote}";
        if (buildNote != null) msg += $"\n\nBuild note: {buildNote}\n" +
                                      "The module wasn't built, so it won't appear in the carousel yet. " +
                                      "Fix the build issue and click ↺ Reload Module to refresh.";

        await ShowMessageDialog("Module created", msg);
    }

    private sealed record NewModuleSpec(string Name, int TileWidth, int TileHeight, string License);

    // Walk up from the running editor's BaseDirectory looking for the
    // editor solution. Mirrors the existing FindVolumeMixerProject /
    // FindDocklyModulesDir patterns.
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

    // A folder qualifies as a clonable module template only if its .csproj is
    // an actual Dockly module project — a class library that references
    // Docklys.ModuleContracts. The "Run*" host apps (RunPattern/RunPlugin/
    // RunTheme) are WinExe projects that don't reference the contracts, so
    // they're excluded and never offered in the template picker.
    private static bool IsModuleProject(string csprojPath)
    {
        try
        {
            return File.ReadAllText(csprojPath)
                       .Contains("Docklys.ModuleContracts", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CreateModule] could not read {csprojPath}: {ex.Message}");
            return false;
        }
    }

    // Local-registry validation only: collisions against sibling project
    // folders and against any DLL already deployed to Dockly's
    // CustomModules. The server-side registry at
    // registry.docklys.qwqc.de is checked separately at submission
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

        var modules = FindDocklyModulesDirStatic();
        if (modules != null)
        {
            var dllPath = Path.Combine(modules, name + ".dll");
            if (File.Exists(dllPath))
                return (false,
                    $"Dockly already has a deployed module DLL named '{name}.dll' in:\n" +
                    $"{modules}\n\nPick a different name, or remove the existing DLL first.");
        }

        return (true, null);
    }

    private static string? FindDocklyModulesDirStatic()
    {
        // Try the standard AppData location first.
        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Docklys", "Modules");
        if (Directory.Exists(appData)) return appData;

        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            foreach (var rel in new[]
                     {
                         Path.Combine("Dockly", "Modules"),
                         Path.Combine("Dockly", "Dockly", "Modules"),
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

    private static void CloneModuleInto(
        string solutionDir,
        string targetDir,
        string newName,
        int tileWidth,
        int tileHeight,
        string templateName)
    {
        var sourceDir = Path.Combine(solutionDir, templateName);
        if (!Directory.Exists(sourceDir))
            throw new DirectoryNotFoundException(
                $"Template folder not found at: {sourceDir}");
        if (Directory.Exists(targetDir))
            throw new IOException($"Target folder already exists: {targetDir}");

        // The template's *real* identifier is its .csproj base name, which is
        // what the namespace/class/x:Class/Id and the .axaml/.cs file names are
        // built from. That casing can differ from the folder name (e.g. the
        // "Spotify" folder ships a "spotify" project), so we replace BOTH the
        // folder name and the project id. Without the project id, the lowercase
        // internals and file names would be left pointing at the template.
        var identifiers = CollectTemplateIdentifiers(sourceDir, templateName);

        Directory.CreateDirectory(targetDir);
        CopyDirectoryFiltered(sourceDir, targetDir, identifiers, templateName, newName);

        // Rename <identifier>.* files to <NewName>.* — the text inside was
        // already rewritten by CopyDirectoryFiltered. Match case-insensitively
        // so e.g. "spotify.csproj" is caught even when the template folder is
        // "Spotify"; strip whichever identifier prefix is longest.
        foreach (var pattern in new[] { "*.csproj", "*.axaml", "*.axaml.cs", "*.cs" })
        {
            foreach (var file in Directory.GetFiles(targetDir, pattern, SearchOption.AllDirectories))
            {
                var baseName = Path.GetFileName(file);
                var prefix = identifiers
                    .Where(id => baseName.StartsWith(id, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(id => id.Length)
                    .FirstOrDefault();
                if (prefix != null)
                {
                    var newBase = newName + baseName.Substring(prefix.Length);
                    var newPath = Path.Combine(Path.GetDirectoryName(file)!, newBase);
                    if (!string.Equals(file, newPath, StringComparison.Ordinal))
                        File.Move(file, newPath);
                }
            }
        }

        // Patch the tile size to the dev's chosen footprint.
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

    private static void GenerateLicenseFile(string targetDir, string license)
    {
        if (string.IsNullOrEmpty(license) || license == "None") return;

        string content = "";
        int year = DateTime.Now.Year;
        
        switch (license)
        {
            case "MIT":
                content = $@"MIT License

Copyright (c) {year}

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the ""Software""), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED ""AS IS"", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.";
                break;
            case "Apache 2.0":
                content = @"Apache License
Version 2.0, January 2004
http://www.apache.org/licenses/

TERMS AND CONDITIONS FOR USE, REPRODUCTION, AND DISTRIBUTION";
                break;
            case "GPLv3":
                content = @"GNU GENERAL PUBLIC LICENSE
Version 3, 29 June 2007

Copyright (C) 2007 Free Software Foundation, Inc. <http://fsf.org/>";
                break;
        }

        if (!string.IsNullOrEmpty(content))
        {
            File.WriteAllText(Path.Combine(targetDir, "LICENSE.txt"), content);
        }
    }

    // Build the set of identifier strings that stand in for the template's
    // name inside its files: the folder name, the .csproj base name (the real
    // namespace/class/Id casing — often differs from the folder), and the
    // DefaultModule scaffold alias. Returned ordered longest-first so a token
    // that is a prefix of another wins when matching file names.
    private static IReadOnlyList<string> CollectTemplateIdentifiers(string sourceDir, string templateName)
    {
        var tokens = new List<string> { templateName };

        var projFile = Directory.EnumerateFiles(sourceDir, "*.csproj").FirstOrDefault();
        if (projFile != null)
        {
            var projId = Path.GetFileNameWithoutExtension(projFile);
            if (!tokens.Contains(projId, StringComparer.Ordinal))
                tokens.Add(projId);
        }

        // DefaultModule's blank scaffold uses "BlackModule" as its class/id alias.
        if (string.Equals(templateName, "DefaultModule", StringComparison.Ordinal))
            tokens.Add("BlackModule");

        return tokens.OrderByDescending(t => t.Length).ToList();
    }

    private static void CopyDirectoryFiltered(
        string src,
        string dst,
        IReadOnlyList<string> identifiers,
        string templateName,
        string newName)
    {
        // One regex over all identifiers so the replacement is a single
        // left-to-right pass: text we just inserted (which may itself contain
        // the template name, e.g. a new module called "MySpotify") is never
        // re-scanned and double-replaced. Identifiers are valid C# names, but
        // escape anyway for safety; longest-first so the broadest token wins.
        var pattern = string.Join("|", identifiers.Select(Regex.Escape));
        var identifierRegex = new Regex(pattern);

        foreach (var entry in Directory.EnumerateFileSystemEntries(src))
        {
            var name = Path.GetFileName(entry);
            if (SkipDirectoryNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                continue;

            var targetPath = Path.Combine(dst, name);
            if (Directory.Exists(entry))
            {
                Directory.CreateDirectory(targetPath);
                CopyDirectoryFiltered(entry, targetPath, identifiers, templateName, newName);
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
                    // Replace every identifier casing (namespace, class, AssemblyName,
                    // x:Class, x:Name, Id string, …) with the new module's name.
                    text = identifierRegex.Replace(text, newName);
                    // DefaultModule's display-name string carries a space, so it
                    // isn't an identifier token — handle it explicitly.
                    if (string.Equals(templateName, "DefaultModule", StringComparison.Ordinal))
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
        w.Classes.Add("dialog");
        w.Background = new SolidColorBrush(Color.Parse("#1A1A1A"));
        w.Foreground = Brushes.White;

        w.Resources["SystemAccentColor"] = Color.Parse("#888888");
        w.Resources["SystemAccentColorLight1"] = Color.Parse("#9A9A9A");
        w.Resources["SystemAccentColorLight2"] = Color.Parse("#ABABAB");
        w.Resources["SystemAccentColorLight3"] = Color.Parse("#BCBCBC");
        w.Resources["SystemAccentColorDark1"] = Color.Parse("#777777");
        w.Resources["SystemAccentColorDark2"] = Color.Parse("#666666");
        w.Resources["SystemAccentColorDark3"] = Color.Parse("#555555");
        w.Resources["ColorAccent"] = new SolidColorBrush(Color.Parse("#E7EAED"));
        w.Resources["ColorAccentBrush"] = new SolidColorBrush(Color.Parse("#E7EAED"));
        w.Resources["SystemControlHighlightAccentBrush"] = new SolidColorBrush(Color.Parse("#E7EAED"));
        w.Resources["SystemControlHighlightAccentBrush2"] = new SolidColorBrush(Color.Parse("#C7CDD3"));
        w.Resources["SystemControlForegroundBaseHighBrush"] = Brushes.White;
        w.Resources["SystemControlForegroundBaseMediumBrush"] = new SolidColorBrush(Color.Parse("#C7CDD3"));
    }
    //
    // Built inline rather than using MessageBox.Avalonia's input API
    // because that package's input-prompt class name moved between v2 and
    // v3, and we want this to work against whatever is referenced today.

    // Step-1 dialog: show all module folders as templates so the user can
    // pick one to copy. DefaultModule (the blank scaffold) is always listed
    // first regardless of alphabetical order.
    private async Task<string?> PromptForTemplateName(string solutionDir)
    {
        // Collect every sibling project folder that has a matching .csproj,
        // using the same exclusion list as the catalog scanner, and only keep
        // folders that are actually Dockly *modules* — not the "Run*" host
        // apps (RunPattern/RunPlugin/RunTheme) that also sit here with a
        // matching .csproj. Cloning one of those as a "template" makes no
        // sense, so they're filtered out.
        var names = new List<string>();
        foreach (var folder in Directory.EnumerateDirectories(solutionDir)
                                        .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
        {
            var folderName = Path.GetFileName(folder);
            if (NonModuleFolders.Contains(folderName)) continue;
            if (folderName.StartsWith(".", StringComparison.Ordinal)) continue;
            var csproj = Path.Combine(folder, folderName + ".csproj");
            if (!File.Exists(csproj)) continue;
            if (!IsModuleProject(csproj)) continue;
            names.Add(folderName);
        }

        // Ensure DefaultModule is always available even if absent from the scan.
        if (!names.Contains("DefaultModule", StringComparer.OrdinalIgnoreCase))
            names.Insert(0, "DefaultModule");

        var tcs = new TaskCompletionSource<string?>();

        // Build one item per template: bold name + small hint for DefaultModule.
        var items = names.Select(n =>
        {
            var label = n == "DefaultModule" ? $"{n}  (blank template)" : n;
            return new TextBlock
            {
                Text = label,
                FontSize = 13,
                Padding = new Thickness(6, 4),
                Foreground = Brushes.White,
            };
        }).ToList<Control>();

        var listBox = new ListBox
        {
            ItemsSource = items,
            SelectedIndex = 0,
            MinHeight = 120,
            MaxHeight = 320,
            Background = new SolidColorBrush(Color.Parse("#111111")),
        };

        var okBtn = new Button
        {
            Content = "Use as Template",
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
            Title = "Choose Template",
            Width = 400,
            MinWidth = 300,
            MinHeight = 200,
            SizeToContent = SizeToContent.Height,
            CanResize = true,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 8,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Select a module to copy as a starting point:",
                        Foreground = Brushes.White,
                        TextWrapping = TextWrapping.Wrap,
                    },
                    listBox,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { okBtn, cancel },
                    },
                },
            },
        };
        StyleDialog(window);

        okBtn.Click += (_, _) =>
        {
            var idx = listBox.SelectedIndex;
            tcs.TrySetResult(idx >= 0 && idx < names.Count ? names[idx] : null);
            window.Close();
        };
        cancel.Click += (_, _) => { tcs.TrySetResult(null); window.Close(); };
        window.Closed += (_, _) => tcs.TrySetResult(null);

        // Double-click also confirms.
        listBox.DoubleTapped += (_, _) =>
        {
            var idx = listBox.SelectedIndex;
            if (idx >= 0 && idx < names.Count)
            {
                tcs.TrySetResult(names[idx]);
                window.Close();
            }
        };

        await window.ShowDialog(this);
        return await tcs.Task;
    }

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

        var licenseBox = new ComboBox
        {
            Width = 320,
            ItemsSource = new[] { "None", "MIT", "Apache 2.0", "GPLv3" },
            SelectedIndex = 0
        };
        var licenseHint = new TextBlock
        {
            Text = "Choose an open-source license. A LICENSE file will be generated automatically.",
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
            MinHeight = 240,
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
                    new TextBlock { Text = "License", Foreground = Brushes.White, Margin = new Thickness(0, 12, 0, 0) },
                    licenseBox,
                    licenseHint,
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
            var license = licenseBox.SelectedItem?.ToString() ?? "None";
            tcs.TrySetResult(new NewModuleSpec(rawName, w, h, license));
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

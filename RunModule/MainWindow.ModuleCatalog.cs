using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Avalonia.Controls;
using Docklys.ModuleContracts;

namespace RunModule;

// Runtime module catalog for the RunModule editor.
//
// Discovery walks the editor solution directory, finds every sibling
// project folder that builds a module DLL, loads each one into the
// default AssemblyLoadContext via LoadFromStream (so the file isn't
// locked), and tracks the IModule UserControl types.
//
// Mirrors what Dockly.Modules.ModuleRegistry does on the host side, but
// scans the *source tree* instead of a CustomModules folder — same idea,
// dev-time variant.
public partial class MainWindow
{
    private sealed record ModuleEntry(
        string FolderName,
        string CsprojPath,
        string DllPath,
        Type ModuleType);

    private readonly List<ModuleEntry> _catalog = new();
    private int _currentIndex;

    // Folders next to RunModule that are part of the editor infrastructure
    // and never carry a module — used to filter the source-tree scan.
    private static readonly HashSet<string> NonModuleFolders =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "RunModule",
            "Docklys.ModuleContracts",
            "OutputModuleDLL",
            "OutputWebPModule",
            ".git",
            ".idea",
            ".vs",
            "bin",
            "obj",
        };

    private void LoadCatalog()
    {
        _catalog.Clear();

        var solutionDir = FindEditorSolutionDir();
        if (solutionDir == null)
        {
            Debug.WriteLine("[Catalog] No editor solution dir reachable from " + AppContext.BaseDirectory);
            return;
        }

        foreach (var folder in Directory.EnumerateDirectories(solutionDir))
        {
            var folderName = Path.GetFileName(folder);
            if (NonModuleFolders.Contains(folderName)) continue;

            var csproj = Path.Combine(folder, folderName + ".csproj");
            if (!File.Exists(csproj)) continue;

            var dll = FindFreshestModuleDll(folder, folderName);
            if (dll == null)
            {
                Debug.WriteLine($"[Catalog] '{folderName}' has no built DLL yet (skipping). " +
                                "Run `dotnet build` on its csproj to make it appear.");
                continue;
            }

            try
            {
                var asm = LoadOrReuseAssembly(folderName, dll);
                foreach (var t in SafeGetTypes(asm))
                {
                    if (IsModuleType(t))
                    {
                        _catalog.Add(new ModuleEntry(folderName, csproj, dll, t));
                        break; // one IModule per project — match Dockly's convention
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Catalog] Failed to load {dll}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Stable, predictable ordering — alphabetical by folder name so
        // arrow navigation feels deterministic across launches.
        _catalog.Sort((a, b) => string.Compare(a.FolderName, b.FolderName, StringComparison.OrdinalIgnoreCase));

        if (_currentIndex >= _catalog.Count) _currentIndex = 0;
    }

    // Re-scan after the source tree changed (Create / Rename) and try to
    // land on the named folder so the user sees the result immediately.
    private void ReloadCatalogAndSelect(string folderName)
    {
        LoadCatalog();
        var idx = _catalog.FindIndex(e =>
            string.Equals(e.FolderName, folderName, StringComparison.OrdinalIgnoreCase));
        _currentIndex = idx >= 0 ? idx : 0;
        ShowModuleAtIndex(_currentIndex);
        UpdateScrollArrowVisibility();
    }

    private void ShowModuleAtIndex(int index)
    {
        var slot = this.FindControl<ContentControl>("ActiveModuleSlot");
        var nameLabel = this.FindControl<TextBlock>("ActiveModuleNameLabel");
        var renameBtn = this.FindControl<Button>("RenameButton");
        if (slot == null) return;

        if (_catalog.Count == 0)
        {
            slot.Content = new TextBlock
            {
                Text = "No modules found. Click ✚ Create Module to scaffold one.",
                Foreground = Avalonia.Media.Brushes.White,
                FontSize = 14,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            if (nameLabel != null) nameLabel.Text = "(no modules)";
            if (renameBtn != null) renameBtn.IsEnabled = false;
            return;
        }

        index = Math.Clamp(index, 0, _catalog.Count - 1);
        _currentIndex = index;
        var entry = _catalog[index];

        try
        {
            // New instance every cycle: matches the user's mental model of
            // "what would my module look like on a fresh launch", which is
            // the whole reason the carousel exists.
            var instance = (Control)Activator.CreateInstance(entry.ModuleType)!;
            slot.Content = instance;

            if (nameLabel != null) nameLabel.Text = entry.FolderName;
            if (renameBtn != null) renameBtn.IsEnabled = true;

            // Re-apply zoom to the new content + re-fit the window. Both
            // are no-ops if the slot or the zoom slider haven't been
            // measured yet.
            var zoom = this.FindControl<Avalonia.Controls.Slider>("ZoomSlider");
            if (zoom != null) ApplyZoomToActiveModule(zoom.Value);

            try { AutoSizeWindow(); } catch (Exception ex) { Debug.WriteLine($"[Catalog] AutoSize failed: {ex.Message}"); }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Catalog] Activator.CreateInstance({entry.ModuleType.FullName}) failed: {ex}");
            slot.Content = new TextBlock
            {
                Text = $"Failed to instantiate {entry.FolderName}:\n{ex.GetType().Name}: {ex.Message}",
                Foreground = Avalonia.Media.Brushes.IndianRed,
                FontSize = 12,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            };
            if (nameLabel != null) nameLabel.Text = entry.FolderName + " (failed)";
        }
    }

    // Pick the freshest DLL inside <folder>/bin/<Config>/<TFM>/<folder>.dll
    // — preferring Debug (the editor's working configuration) but falling
    // back to Release if that's all that exists.
    private static string? FindFreshestModuleDll(string projectFolder, string folderName)
    {
        var binDir = Path.Combine(projectFolder, "bin");
        if (!Directory.Exists(binDir)) return null;

        try
        {
            var matches = Directory.EnumerateFiles(binDir, folderName + ".dll", SearchOption.AllDirectories)
                                   .Select(p => new FileInfo(p))
                                   .Where(fi => fi.Length > 0)
                                   .OrderByDescending(fi => fi.LastWriteTimeUtc)
                                   .ToList();
            return matches.FirstOrDefault()?.FullName;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Catalog] DLL search under {binDir} failed: {ex.Message}");
            return null;
        }
    }

    // Avoid loading the same assembly twice. Project-referenced modules
    // are already in the default ALC (RunModule.csproj pulls them in),
    // and re-reading them via LoadFromStream would create a duplicate
    // Assembly with the same identity — modules then resolve to whichever
    // copy of IModule was used at compile time, which breaks cross-assembly
    // interface lookups.
    private static Assembly LoadOrReuseAssembly(string expectedName, string dllPath)
    {
        var existing = AssemblyLoadContext.Default.Assemblies.FirstOrDefault(a =>
            string.Equals(a.GetName().Name, expectedName, StringComparison.OrdinalIgnoreCase));
        if (existing != null) return existing;

        // Load via stream so the file on disk isn't locked — lets the user
        // rebuild a module's csproj while the editor is still running.
        var bytes = File.ReadAllBytes(dllPath);
        using var ms = new MemoryStream(bytes);
        return AssemblyLoadContext.Default.LoadFromStream(ms);
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly asm)
    {
        try { return asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null)!; }
    }

    private static bool IsModuleType(Type t) =>
        t is { IsAbstract: false, IsClass: true }
        && typeof(Control).IsAssignableFrom(t)
        && t.GetInterfaces().Any(i => i.Name == nameof(IModule));
}

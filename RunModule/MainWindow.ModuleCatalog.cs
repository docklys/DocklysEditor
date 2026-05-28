using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Avalonia.Controls;
using Docklys.ModuleContracts;

namespace RunModule;

// Runtime module catalog for the RunModule editor.
//
// Discovery walks the editor solution directory, finds every sibling project
// folder that builds a module DLL, and loads each one into its own isolated,
// collectable AssemblyLoadContext (ModuleLoadContext). Isolation means the
// Reload button can unload the old context and load a freshly-built DLL
// without restarting the editor. Two instances from the same context share
// static members (e.g. VolumeMixer's GroupVolumeChanged sync event), which
// is exactly what the dual-view feature needs.
public partial class MainWindow
{
    private sealed record ModuleEntry(
        string FolderName,
        string CsprojPath,
        string DllPath,
        Type ModuleType,
        ModuleLoadContext LoadContext);

    private readonly List<ModuleEntry> _catalog = new();
    private int _currentIndex;
    private bool _dualView;

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
        // Unload old isolated contexts before discarding entries — allows the
        // GC to collect old module types and free any native resources they hold.
        foreach (var entry in _catalog)
            TryUnloadContext(entry.LoadContext);
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

            ModuleLoadContext? ctx = null;
            try
            {
                ctx = new ModuleLoadContext(dll);
                var asm = ctx.LoadModule();
                Type? found = null;
                foreach (var t in SafeGetTypes(asm))
                {
                    if (IsModuleType(t)) { found = t; break; }
                }

                if (found != null)
                {
                    _catalog.Add(new ModuleEntry(folderName, csproj, dll, found, ctx));
                    ctx = null; // ownership transferred to catalog entry
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Catalog] Failed to load {dll}: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                // Only reached (non-null) when no IModule type was found or an exception occurred.
                TryUnloadContext(ctx);
            }
        }

        // Stable, predictable ordering — alphabetical by folder name.
        _catalog.Sort((a, b) => string.Compare(a.FolderName, b.FolderName, StringComparison.OrdinalIgnoreCase));

        if (_currentIndex >= _catalog.Count) _currentIndex = 0;
    }

    // Re-scan after the source tree changed (Create / Rename) and try to
    // land on the named folder so the user sees the result immediately.
    private void ReloadCatalogAndSelect(string folderName)
    {
        // Drop slot contents first so module instances are detached and the
        // old isolated contexts can be fully collected after Unload().
        ClearModuleSlots();

        LoadCatalog();
        var idx = _catalog.FindIndex(e =>
            string.Equals(e.FolderName, folderName, StringComparison.OrdinalIgnoreCase));
        _currentIndex = idx >= 0 ? idx : 0;
        ShowModuleAtIndex(_currentIndex);
        UpdateScrollArrowVisibility();
    }

    private void ClearModuleSlots()
    {
        var slot = this.FindControl<ContentControl>("ActiveModuleSlot");
        if (slot != null) slot.Content = null;
        var slot2 = this.FindControl<ContentControl>("SecondModuleSlot");
        if (slot2 != null) slot2.Content = null;
    }

    private void ShowModuleAtIndex(int index)
    {
        var slot = this.FindControl<ContentControl>("ActiveModuleSlot");
        var slot2 = this.FindControl<ContentControl>("SecondModuleSlot");
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
            if (slot2 != null) { slot2.Content = null; slot2.IsVisible = false; }
            if (nameLabel != null) nameLabel.Text = "(no modules)";
            if (renameBtn != null) renameBtn.IsEnabled = false;
            return;
        }

        index = Math.Clamp(index, 0, _catalog.Count - 1);
        _currentIndex = index;
        var entry = _catalog[index];

        try
        {
            var projFolder = Path.GetDirectoryName(entry.CsprojPath)!;
            var (currentW, currentH) = ReadCurrentTileSize(projFolder);

            var instance = (Control)Activator.CreateInstance(entry.ModuleType)!;
            if (instance is IResizable resizable)
            {
                resizable.TileResizeRequested += (w, h) => OnModuleResizeRequested(w, h);
                resizable.SetTileSize(currentW, currentH);
            }
            slot.Content = instance;

            if (nameLabel != null) nameLabel.Text = entry.FolderName;
            if (renameBtn != null) renameBtn.IsEnabled = true;

            // Dual-view: show a second independent instance in slot2.
            // Both instances come from the same isolated ALC, so they share
            // static members (GroupVolumeChanged, etc.) and will sync.
            if (slot2 != null)
            {
                if (_dualView)
                {
                    var instance2 = (Control)Activator.CreateInstance(entry.ModuleType)!;
                    if (instance2 is IResizable resizable2)
                    {
                        resizable2.TileResizeRequested += (w, h) => OnModuleResizeRequested(w, h);
                        resizable2.SetTileSize(currentW, currentH);
                    }
                    slot2.Content = instance2;
                    slot2.IsVisible = true;
                }
                else
                {
                    slot2.Content = null;
                    slot2.IsVisible = false;
                }
            }

            // Re-apply zoom to the new content + re-fit the window.
            var zoom = this.FindControl<Avalonia.Controls.Slider>("ZoomSlider");
            if (zoom != null) ApplyZoomToActiveModule(zoom.Value);

            try { AutoSizeWindow(); } catch (Exception ex) { Debug.WriteLine($"[Catalog] AutoSize failed: {ex.Message}"); }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Catalog] Activator.CreateInstance({entry.ModuleType.FullName}) failed: {ex}");
            var errorText = BuildFullErrorText(entry.FolderName, ex);
            slot.Content = BuildErrorPanel(errorText, slot);
            if (nameLabel != null) nameLabel.Text = entry.FolderName + " (failed)";
        }
    }

    // Pick the freshest DLL inside <folder>/bin/<Config>/<TFM>/<folder>.dll
    // — preferring Debug but falling back to Release if that's all that exists.
    private static string BuildFullErrorText(string folderName, Exception ex)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Failed to instantiate {folderName}:");
        var current = ex;
        while (current != null)
        {
            sb.AppendLine($"{current.GetType().FullName}: {current.Message}");
            current = current.InnerException;
            if (current != null) sb.Append("  → ");
        }
        return sb.ToString().TrimEnd();
    }

    private static Avalonia.Controls.Control BuildErrorPanel(string errorText, ContentControl host)
    {
        var copyBtn = new Button { Content = "Copy error", Margin = new Avalonia.Thickness(0, 0, 0, 6) };
        copyBtn.Click += async (_, _) =>
        {
            var clipboard = TopLevel.GetTopLevel(host)?.Clipboard;
            if (clipboard != null) await clipboard.SetTextAsync(errorText);
        };
        var panel = new StackPanel { Margin = new Avalonia.Thickness(8) };
        panel.Children.Add(copyBtn);
        panel.Children.Add(new TextBlock
        {
            Text         = errorText,
            Foreground   = Avalonia.Media.Brushes.IndianRed,
            FontSize     = 12,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });
        return panel;
    }

    internal static string? FindFreshestModuleDll(string projectFolder, string folderName)
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

    private static void TryUnloadContext(ModuleLoadContext? ctx)
    {
        if (ctx == null) return;
        try { ctx.Unload(); }
        catch (Exception ex) { Debug.WriteLine($"[Catalog] ALC unload failed: {ex.Message}"); }
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

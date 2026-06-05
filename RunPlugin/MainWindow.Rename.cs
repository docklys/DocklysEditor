using System;
using System.Diagnostics;
using System.IO;
using Avalonia.Interactivity;

namespace RunPlugin;

// "Rename Plugin" — renames the active plugin's folder + .csproj/Plugin.cs,
// find-and-replaces the old identifier inside the copied files (class names,
// PluginName, UniquePluginId), patches RunPlugin.csproj's <ProjectReference>,
// updates DefaultModule.sln (remove → add), rebuilds, and refreshes the catalog.
//
// Plugin DLLs are loaded via LoadFromStream (see PluginLoadContext) so the files
// on disk are never locked — rename can run while the preview is live.
public partial class MainWindow
{
    private async void RenamePlugin_Click(object? sender, RoutedEventArgs e)
    {
        if (_catalog.Count == 0)
        {
            await ShowMessageDialog("Rename Plugin", "No plugin is currently displayed.");
            return;
        }

        var solutionDir = FindEditorSolutionDir();
        var pluginsDir = GetPluginsSourceDir();
        if (solutionDir == null || pluginsDir == null)
        {
            await ShowMessageDialog("Rename Plugin",
                "Could not locate the editor solution root from " + AppContext.BaseDirectory);
            return;
        }

        var current = _catalog[_currentIndex];
        var oldName = current.FolderName;

        var requested = await PromptForPluginName(initial: oldName, title: "Rename Plugin");
        if (string.IsNullOrWhiteSpace(requested)) return;
        var newName = requested.Trim();
        if (string.Equals(newName, oldName, StringComparison.Ordinal)) return; // no-op

        var (ok, reason) = ValidatePluginName(newName, pluginsDir, allowExistingFolder: oldName);
        if (!ok)
        {
            await ShowMessageDialog("Invalid name", reason!);
            return;
        }

        string oldDir = Path.Combine(pluginsDir, oldName);
        string newDir = Path.Combine(pluginsDir, newName);

        try
        {
            RenamePluginOnDisk(oldDir, newDir, oldName, newName);
        }
        catch (Exception ex)
        {
            await ShowMessageDialog("Rename failed",
                $"Could not rename plugin on disk:\n\n{ex.GetType().Name}: {ex.Message}");
            return;
        }

        var slnPath = Path.Combine(solutionDir, "DefaultModule.sln");
        await TryRemoveFromSolution(slnPath, Path.Combine(oldDir, oldName + ".csproj"));
        var slnAddNote = await TryAddToSolution(slnPath, Path.Combine(newDir, newName + ".csproj"));

        var refNote = TryRetargetProjectReferenceInRunPlugin(solutionDir, oldName, newName);
        var buildNote = await TryBuildProject(Path.Combine(newDir, newName + ".csproj"));

        ReloadCatalogAndSelect(newName);

        var msg = $"Plugin renamed: {oldName} → {newName}";
        if (slnAddNote != null) msg += $"\n\nSolution note: {slnAddNote}";
        if (refNote != null) msg += $"\n\nRunPlugin.csproj note: {refNote}";
        if (buildNote != null) msg += $"\n\nBuild note: {buildNote}";
        await ShowMessageDialog("Plugin renamed", msg);
    }

    private static void RenamePluginOnDisk(string oldDir, string newDir, string oldName, string newName)
    {
        if (!Directory.Exists(oldDir))
            throw new DirectoryNotFoundException($"Source folder not found: {oldDir}");

        // bin/obj carry the old <oldName>.dll/.pdb — delete so the build regenerates clean.
        TryDeleteSubdir(oldDir, "bin");
        TryDeleteSubdir(oldDir, "obj");

        // Handle case-only renames (foo → Foo) on Windows via a temp hop.
        var caseOnlyRename = string.Equals(oldDir, newDir, StringComparison.OrdinalIgnoreCase)
                             && !string.Equals(oldDir, newDir, StringComparison.Ordinal);
        if (caseOnlyRename)
        {
            var tempDir = newDir + ".rename." + Guid.NewGuid().ToString("N").Substring(0, 8);
            Directory.Move(oldDir, tempDir);
            Directory.Move(tempDir, newDir);
        }
        else
        {
            if (Directory.Exists(newDir))
                throw new IOException($"Target folder already exists: {newDir}");
            Directory.Move(oldDir, newDir);
        }

        // Rename <oldName>.* files to <newName>.* inside the moved folder.
        foreach (var pattern in new[] { "*.csproj", "*.cs" })
        {
            foreach (var file in Directory.GetFiles(newDir, pattern, SearchOption.AllDirectories))
            {
                var baseName = Path.GetFileName(file);
                if (baseName.StartsWith(oldName, StringComparison.OrdinalIgnoreCase))
                {
                    var newBase = newName + baseName.Substring(oldName.Length);
                    var newPath = Path.Combine(Path.GetDirectoryName(file)!, newBase);
                    if (!string.Equals(file, newPath, StringComparison.Ordinal))
                        File.Move(file, newPath);
                }
            }
        }

        // Find/replace the identifier inside every non-binary file (class names,
        // PluginName, UniquePluginId, etc.).
        foreach (var file in Directory.GetFiles(newDir, "*", SearchOption.AllDirectories))
        {
            try
            {
                var text = File.ReadAllText(file);
                var rewritten = text.Replace(oldName, newName);
                if (rewritten != text) File.WriteAllText(file, rewritten);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Rename] Could not rewrite {file}: {ex.Message}");
            }
        }
    }

    private static void TryDeleteSubdir(string parent, string name)
    {
        var path = Path.Combine(parent, name);
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception ex) { Debug.WriteLine($"[Rename] Could not delete {path}: {ex.Message}"); }
    }
}

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RunModule;

// "Rename Module" — renames the active module's folder + .csproj/.axaml/
// .axaml.cs files, find-and-replaces the old identifier inside the
// copied files, updates DefaultModule.sln (remove → add), patches any
// matching <ProjectReference> in RunModule.csproj, rebuilds the project,
// and refreshes the catalog so the renamed module re-appears in the
// carousel under its new name.
//
// Module DLLs are loaded via Assembly.LoadFromStream (see ModuleCatalog),
// so the files on disk are never locked — rename can run while the
// editor is still showing the old name.
public partial class MainWindow
{
    private async void RenameModule_Click(object? sender, RoutedEventArgs e)
    {
        if (_catalog.Count == 0)
        {
            await ShowMessageDialog("Rename Module", "No module is currently displayed.");
            return;
        }

        var solutionDir = FindEditorSolutionDir();
        if (solutionDir == null)
        {
            await ShowMessageDialog("Rename Module",
                "Could not locate the DocklysModuleEditor solution root from " +
                AppContext.BaseDirectory);
            return;
        }

        var current = _catalog[_currentIndex];
        var oldName = current.FolderName;

        var requested = await PromptForModuleName(initial: oldName, title: "Rename Module");
        if (string.IsNullOrWhiteSpace(requested)) return;
        var newName = requested.Trim();

        if (string.Equals(newName, oldName, StringComparison.Ordinal))
            return; // no-op

        var (ok, reason) = ValidateModuleName(newName, solutionDir, allowExistingFolder: oldName);
        if (!ok)
        {
            await ShowMessageDialog("Invalid name", reason!);
            return;
        }

        string oldDir = Path.Combine(solutionDir, oldName);
        string newDir = Path.Combine(solutionDir, newName);

        try
        {
            RenameModuleOnDisk(oldDir, newDir, oldName, newName);
        }
        catch (Exception ex)
        {
            await ShowMessageDialog("Rename failed",
                $"Could not rename module on disk:\n\n{ex.GetType().Name}: {ex.Message}");
            return;
        }

        // Solution file: remove the old entry, add the new one. dotnet sln
        // handles the GUID + ProjectConfigurationPlatforms entries for us.
        var slnPath = Path.Combine(solutionDir, "DefaultModule.sln");
        await TryRemoveFromSolution(slnPath, Path.Combine(oldDir, oldName + ".csproj"));
        var slnAddNote = await TryAddToSolution(slnPath, Path.Combine(newDir, newName + ".csproj"));

        // RunModule.csproj may still reference the old project — patch it
        // so the next solution build doesn't fail with "project not found".
        var refNote = TryRetargetProjectReferenceInRunModule(solutionDir, oldName, newName);

        // Build the renamed project so a fresh <NewName>.dll exists for
        // catalog discovery.
        var buildNote = await TryBuildProject(Path.Combine(newDir, newName + ".csproj"));

        // Re-discover and switch the carousel to the renamed module.
        ReloadCatalogAndSelect(newName);

        var msg = $"Module renamed: {oldName} → {newName}";
        if (slnAddNote != null) msg += $"\n\nSolution note: {slnAddNote}";
        if (refNote != null) msg += $"\n\nRunModule.csproj note: {refNote}";
        if (buildNote != null) msg += $"\n\nBuild note: {buildNote}";
        await ShowMessageDialog("Module renamed", msg);
    }

    private static void RenameModuleOnDisk(string oldDir, string newDir, string oldName, string newName)
    {
        if (!Directory.Exists(oldDir))
            throw new DirectoryNotFoundException($"Source folder not found: {oldDir}");

        // bin/obj contain the old <oldName>.dll/.pdb/etc. They'd survive
        // the rename + show up under the new folder as stale ghosts, so
        // delete them before the move and let dotnet build regenerate.
        TryDeleteSubdir(oldDir, "bin");
        TryDeleteSubdir(oldDir, "obj");

        // Case-only rename on Windows: Directory.Move("hello", "Hello")
        // works on NTFS but fails if oldDir and newDir refer to the same
        // path. Detect and use a two-step move via a unique temp name.
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

        // Rename the four DefaultModule-style files inside the moved folder.
        foreach (var pattern in new[] { "*.csproj", "*.axaml", "*.axaml.cs", "*.cs" })
        {
            foreach (var file in Directory.GetFiles(newDir, pattern, SearchOption.AllDirectories))
            {
                var baseName = Path.GetFileName(file);
                if (baseName.StartsWith(oldName + ".", StringComparison.OrdinalIgnoreCase))
                {
                    var newBase = newName + baseName.Substring(oldName.Length);
                    var newPath = Path.Combine(Path.GetDirectoryName(file)!, newBase);
                    File.Move(file, newPath);
                }
            }
        }

        // Find/replace the identifier inside every non-binary file.
        foreach (var file in Directory.GetFiles(newDir, "*", SearchOption.AllDirectories))
        {
            if (IsBinary(file)) continue;
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

    private static async Task<string?> TryRemoveFromSolution(string slnPath, string projPath)
    {
        if (!File.Exists(slnPath)) return $"Solution file not found at {slnPath}.";
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"sln \"{slnPath}\" remove \"{projPath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(slnPath)!,
            };
            using var p = Process.Start(psi);
            if (p == null) return "Could not launch the dotnet CLI.";
            await p.StandardOutput.ReadToEndAsync();
            await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            // Non-zero is fine — the project may already be missing because
            // the folder was renamed. Caller logs but doesn't surface this.
            return null;
        }
        catch (Exception ex)
        {
            return $"`dotnet sln remove` threw: {ex.GetType().Name}: {ex.Message}";
        }
    }

    private static string? TryRetargetProjectReferenceInRunModule(string solutionDir, string oldName, string newName)
    {
        var csproj = Path.Combine(solutionDir, "RunModule", "RunModule.csproj");
        if (!File.Exists(csproj)) return null;

        var text = File.ReadAllText(csproj);
        var oldInclude = $"..\\{oldName}\\{oldName}.csproj";
        var newInclude = $"..\\{newName}\\{newName}.csproj";

        if (text.IndexOf(oldInclude, StringComparison.OrdinalIgnoreCase) < 0) return null;

        text = text.Replace(oldInclude, newInclude, StringComparison.OrdinalIgnoreCase);
        File.WriteAllText(csproj, text);
        return null;
    }

    private static async Task<string?> TryBuildProject(string csprojPath)
    {
        if (!File.Exists(csprojPath)) return $"csproj not found at {csprojPath}.";
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"build \"{csprojPath}\" -c Debug -nologo -v minimal",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(csprojPath)!,
            };
            using var p = Process.Start(psi);
            if (p == null) return "Could not launch the dotnet CLI.";
            var stdout = await p.StandardOutput.ReadToEndAsync();
            var stderr = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            if (p.ExitCode != 0)
            {
                var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                return $"`dotnet build` exited with {p.ExitCode}: {detail.Trim()}";
            }
            return null;
        }
        catch (Exception ex)
        {
            return $"`dotnet build` threw: {ex.GetType().Name}: {ex.Message}";
        }
    }
}

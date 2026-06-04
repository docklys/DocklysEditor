using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace RunPattern;

// Shared project/solution plumbing for the Rename + Delete + New flows: name
// validation, RunPattern.csproj <ProjectReference> patching, best-effort
// DefaultModule.sln edits, dotnet build, and catalog refresh. Mirrors the
// equivalents in RunModule.
public partial class MainWindow
{
    private static readonly Regex PatternIdentifierRegex =
        new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    // Folder names we never let a pattern take.
    private static readonly string[] ReservedPatternNames =
        { "RunPattern", "Docklys.ModuleContracts" };

    private void ReloadCatalogAndSelect(string folderName)
    {
        LoadCatalog();
        var idx = _catalog.FindIndex(c => string.Equals(c.FolderName, folderName, StringComparison.OrdinalIgnoreCase));
        if (idx < 0) idx = Math.Min(_currentIndex, Math.Max(0, _catalog.Count - 1));
        ShowPatternAtIndex(idx);
    }

    private static (bool ok, string? reason) ValidatePatternName(
        string name, string patternsDir, string? allowExistingFolder = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            return (false, "Pattern name cannot be empty.");
        if (!PatternIdentifierRegex.IsMatch(name))
            return (false,
                "Pattern name must be a valid identifier — start with a letter or " +
                "underscore, then letters/digits/underscores. No spaces or special characters.");
        if (ReservedPatternNames.Any(r => string.Equals(r, name, StringComparison.OrdinalIgnoreCase)))
            return (false, $"'{name}' is reserved. Pick a different name.");

        if (Directory.Exists(patternsDir))
        {
            foreach (var folder in Directory.EnumerateDirectories(patternsDir))
            {
                var folderName = Path.GetFileName(folder);
                if (allowExistingFolder != null
                    && string.Equals(folderName, allowExistingFolder, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (string.Equals(folderName, name, StringComparison.OrdinalIgnoreCase))
                    return (false, $"A pattern folder named '{folderName}' already exists. Pick a different name.");
            }
        }

        return (true, null);
    }

    // RunPattern.csproj only references the bundled patterns; user-scaffolded
    // ones aren't listed, so these patches are best-effort no-ops when absent.
    private static string? TryRetargetProjectReferenceInRunPattern(string solutionDir, string oldName, string newName)
    {
        var csproj = Path.Combine(solutionDir, "RunPattern", "RunPattern.csproj");
        if (!File.Exists(csproj)) return null;

        var text = File.ReadAllText(csproj);
        var oldInclude = $"..\\Patterns\\{oldName}\\{oldName}.csproj";
        var newInclude = $"..\\Patterns\\{newName}\\{newName}.csproj";
        if (text.IndexOf(oldInclude, StringComparison.OrdinalIgnoreCase) < 0) return null;

        text = text.Replace(oldInclude, newInclude, StringComparison.OrdinalIgnoreCase);
        File.WriteAllText(csproj, text);
        return null;
    }

    private static void StripProjectReferenceFromRunPattern(string solutionDir, string folderName)
    {
        var csproj = Path.Combine(solutionDir, "RunPattern", "RunPattern.csproj");
        if (!File.Exists(csproj)) return;

        var text = File.ReadAllText(csproj);
        var marker = $"<ProjectReference Include=\"..\\Patterns\\{folderName}\\{folderName}.csproj\" />";
        var idx = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return;

        var lineStart = text.LastIndexOf('\n', idx) + 1;
        var lineEnd = text.IndexOf('\n', idx);
        if (lineEnd < 0) lineEnd = text.Length; else lineEnd++;

        text = text.Remove(lineStart, lineEnd - lineStart);
        File.WriteAllText(csproj, text);
    }

    private static async Task<string?> TryRemoveFromSolution(string slnPath, string projPath)
    {
        if (!File.Exists(slnPath)) return null;
        try
        {
            using var p = StartDotnet($"sln \"{slnPath}\" remove \"{projPath}\"", Path.GetDirectoryName(slnPath)!);
            if (p == null) return "Could not launch the dotnet CLI.";
            await p.StandardOutput.ReadToEndAsync();
            await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            return null; // best-effort
        }
        catch (Exception ex) { return $"`dotnet sln remove` threw: {ex.GetType().Name}: {ex.Message}"; }
    }

    private static async Task<string?> TryAddToSolution(string slnPath, string projPath)
    {
        if (!File.Exists(slnPath)) return $"Solution file not found at {slnPath}.";
        try
        {
            using var p = StartDotnet($"sln \"{slnPath}\" add \"{projPath}\"", Path.GetDirectoryName(slnPath)!);
            if (p == null) return "Could not launch the dotnet CLI.";
            var stderr = await p.StandardError.ReadToEndAsync();
            var stdout = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            if (p.ExitCode != 0)
                return $"`dotnet sln add` exited with {p.ExitCode}: {(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr).Trim()}";
            return null;
        }
        catch (Exception ex) { return $"`dotnet sln add` threw: {ex.GetType().Name}: {ex.Message}"; }
    }

    private static async Task<string?> TryBuildProject(string csprojPath)
    {
        if (!File.Exists(csprojPath)) return $"csproj not found at {csprojPath}.";
        try
        {
            using var p = StartDotnet($"build \"{csprojPath}\" -c Debug -nologo -v minimal", Path.GetDirectoryName(csprojPath)!);
            if (p == null) return "Could not launch the dotnet CLI.";
            var stdout = await p.StandardOutput.ReadToEndAsync();
            var stderr = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            if (p.ExitCode != 0)
                return $"`dotnet build` exited with {p.ExitCode}: {(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr).Trim()}";
            return null;
        }
        catch (Exception ex) { return $"`dotnet build` threw: {ex.GetType().Name}: {ex.Message}"; }
    }

    private static Process? StartDotnet(string arguments, string workingDir) =>
        Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDir,
        });
}

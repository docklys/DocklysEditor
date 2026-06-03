using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace RunModule;

// Resolving the Dockly Modules folder. Order of attempts:
//   1. Standard AppData location (%APPDATA%\Docklys\Modules) — the primary
//      deploy target.
//   2. Walk up from BaseDirectory + check running Dockly process exe dir.
//   3. Saved path in %APPDATA%/Docklys/RunModule.json (set by a previous
//      run of the picker).
//   4. Folder picker — prompt the dev to point at the Dockly install
//      directory. Accepts either the install root (containing
//      Modules/) OR the Modules folder directly.
// Success at any step is persisted so the dev never has to pick twice.
public partial class MainWindow
{
    // Returns every Dockly Modules directory reachable from the
    // current machine. The Push-to-Docklys flow copies the built DLL to
    // *all* of them, because Dockly's ModuleRegistry can resolve to any
    // of these at runtime depending on which exist:
    //
    //   1. Standard AppData location.
    //   2. Source-tree folders (Dockly/Modules, Docklys/Dockly/Modules, …)
    //      — what walking up the dev tree finds.
    //   3. Bin-output folders (Dockly.Desktop/bin/Debug/net9.0/Modules,
    //      Dockly/bin/…/Modules, …) — what AppContext.BaseDirectory
    //      points at when Dockly is actually running.
    //   4. The running Dockly process's exe directory's Modules.
    //   5. The install root the dev previously picked + any Modules
    //      under it.
    //
    // Deduplicated by case-insensitive path.
    private List<string> FindAllDocklyModulesDirs()
    {
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void TryAdd(string? p)
        {
            if (string.IsNullOrWhiteSpace(p)) return;
            try
            {
                var full = Path.GetFullPath(p);
                if (Directory.Exists(full)) dirs.Add(full);
            }
            catch { /* bad path — skip */ }
        }

        // (1) Standard AppData location — prioritize this as the primary target.
        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Docklys", "Modules");
        if (!Directory.Exists(appData))
        {
            try { Directory.CreateDirectory(appData); }
            catch { /* ignore */ }
        }
        TryAdd(appData);

        // (2) Walk up looking for known source-tree Modules layouts.
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            foreach (var rel in new[]
                     {
                         Path.Combine("Dockly", "Modules"),
                         Path.Combine("Dockly", "Dockly", "Modules"),
                         Path.Combine("Docklys", "Dockly", "Modules"),
                         // Compatibility for older installs
                         Path.Combine("Dockly", "CustomModules"),
                         Path.Combine("Dockly", "Dockly", "CustomModules"),
                         Path.Combine("Docklys", "Dockly", "CustomModules"),
                     })
            {
                TryAdd(Path.Combine(dir, rel));
            }
            var parent = Directory.GetParent(dir);
            if (parent == null) break;
            dir = parent.FullName;
        }

        // (3) Walk up looking for *any* Modules under any Dockly
        //     project's bin output — these are the paths Dockly actually
        //     reads from once it's been launched at least once.
        dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            foreach (var projRel in new[]
                     {
                         Path.Combine("Dockly", "Dockly.Desktop"),
                         Path.Combine("Dockly", "Dockly"),
                         Path.Combine("Docklys", "Dockly", "Dockly.Desktop"),
                         "Dockly.Desktop",
                         "Dockly",
                     })
            {
                var binPath = Path.Combine(dir, projRel, "bin");
                if (!Directory.Exists(binPath)) continue;
                try
                {
                    foreach (var m in Directory.GetDirectories(binPath, "Modules", SearchOption.AllDirectories))
                        TryAdd(m);
                    foreach (var cm in Directory.GetDirectories(binPath, "CustomModules", SearchOption.AllDirectories))
                        TryAdd(cm);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Catalog] bin scan under {binPath} failed: {ex.Message}");
                }
            }
            var parent = Directory.GetParent(dir);
            if (parent == null) break;
            dir = parent.FullName;
        }

        // (4) Running Dockly process's exe dir.
        try
        {
            var dockly = Process.GetProcessesByName("Dockly").FirstOrDefault()
                         ?? Process.GetProcessesByName("Dockly.Desktop").FirstOrDefault();
            var exe = dockly?.MainModule?.FileName;
            if (exe != null)
            {
                var exeDir = Path.GetDirectoryName(exe);
                if (exeDir != null)
                {
                    var m = Path.Combine(exeDir, "Modules");
                    Directory.CreateDirectory(m); // first deploy onto a fresh install
                    TryAdd(m);
                    
                    var cm = Path.Combine(exeDir, "CustomModules");
                    TryAdd(cm);
                }
            }
        }
        catch (Exception ex) { Debug.WriteLine($"[Catalog] running-process probe failed: {ex.Message}"); }

        // (5) Saved install dir + any Modules under it.
        var saved = SkinHost.LoadDocklyInstallDir();
        if (!string.IsNullOrWhiteSpace(saved) && Directory.Exists(saved))
        {
            TryAdd(ValidateAndResolveDocklyDir(saved));
            try
            {
                foreach (var m in Directory.GetDirectories(saved, "Modules", SearchOption.AllDirectories))
                    TryAdd(m);
                foreach (var cm in Directory.GetDirectories(saved, "CustomModules", SearchOption.AllDirectories))
                    TryAdd(cm);
            }
            catch (Exception ex) { Debug.WriteLine($"[Catalog] saved-dir scan failed: {ex.Message}"); }
        }

        return dirs.ToList();
    }

    private async Task<string?> ResolveDocklyModulesDirAsync(bool interactiveOnMiss)
    {
        // FindDocklyModulesDir already covers: walk-up source tree,
        // running Dockly process, AND the saved install path from the
        // previous picker. If it still returns null, the dev needs to
        // tell us where Dockly is.
        var auto = FindDocklyModulesDir();
        if (auto != null) return auto;

        if (!interactiveOnMiss) return null;
        return await PickDocklyInstallFolderAsync();
    }

    // Accept either the Dockly install root (folder containing
    // Modules) or Modules itself. Returns the path to
    // Modules, or null if the input doesn't qualify.
    private static string? ValidateAndResolveDocklyDir(string? candidatePath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath)) return null;
        if (!Directory.Exists(candidatePath)) return null;

        var folderName = Path.GetFileName(candidatePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        // User pointed at the Modules folder directly.
        if (string.Equals(folderName, "Modules", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(folderName, "CustomModules", StringComparison.OrdinalIgnoreCase))
            return candidatePath;

        // User pointed at the install root — the Modules subfolder
        // is what we need to write into.
        var nested = Path.Combine(candidatePath, "Modules");
        if (Directory.Exists(nested)) return nested;
        
        var nestedCustom = Path.Combine(candidatePath, "CustomModules");
        if (Directory.Exists(nestedCustom)) return nestedCustom;

        // Common dev layout: <root>\Dockly\Modules.
        var deepNested = Path.Combine(candidatePath, "Dockly", "Modules");
        if (Directory.Exists(deepNested)) return deepNested;
        
        var deepNestedCustom = Path.Combine(candidatePath, "Dockly", "CustomModules");
        if (Directory.Exists(deepNestedCustom)) return deepNestedCustom;

        return null;
    }

    private async Task<string?> PickDocklyInstallFolderAsync()
    {
        // Friendly heads-up before the picker so the dev knows what to
        // navigate to — picking the wrong folder would silently fail
        // validation otherwise.
        await ShowMessageDialog("Locate Docklys",
            "Dockly's install folder couldn't be found automatically.\n\n" +
            "Please pick the folder Dockly is installed in — the one that " +
            "contains a 'Modules' subfolder (or the Modules " +
            "folder itself). Your choice is remembered for next time.");

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return null;

        IStorageFolder? startLocation = null;
        try
        {
            // Suggest a sensible starting point — last saved dir if any,
            // otherwise the user's profile root.
            var seed = SkinHost.LoadDocklyInstallDir();
            if (!string.IsNullOrWhiteSpace(seed) && Directory.Exists(seed))
                startLocation = await topLevel.StorageProvider.TryGetFolderFromPathAsync(seed);
        }
        catch (Exception ex) { Debug.WriteLine($"[DocklyPath] seed lookup failed: {ex.Message}"); }

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Docklys install folder",
            AllowMultiple = false,
            SuggestedStartLocation = startLocation,
        });

        if (folders == null || folders.Count == 0) return null;
        var picked = folders[0].TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(picked)) return null;

        var resolved = ValidateAndResolveDocklyDir(picked);
        if (resolved == null)
        {
            await ShowMessageDialog("Wrong folder",
                $"'{picked}' doesn't look like a Docklys install — no " +
                "'Modules' subfolder found. Pick the folder Dockly " +
                "is installed in, or the Modules folder directly.");
            return null;
        }

        // Save the *install root* the user picked, not the resolved
        // Modules child — so re-validation can also work if the
        // user reinstalls and the install root path is what we should
        // probe for future Modules.
        SkinHost.SaveDocklyInstallDir(picked);
        return resolved;
    }
}

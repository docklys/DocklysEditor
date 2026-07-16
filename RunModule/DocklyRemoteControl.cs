using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace RunModule;

// Talks to a running Dockly instance (or instances) via the same tiny command-file IPC
// Dockly already uses for its Wayland hotkey watcher (Dockly/Views/MainWindow.RemoteControl.cs
// watches %APPDATA%/Docklys/commands for *.cmd files). Dockly's ModuleRegistry only rescans its
// Modules directories at process startup or from the in-app "Import Module" button — there is no
// filesystem watcher on the Modules folder itself — so copying a fresh DLL into an already-running
// Dockly used to be a silent no-op until the user manually restarted it. Dropping a command here
// is what actually makes a pushed module show up without a restart.
internal static class DocklyRemoteControl
{
    private static readonly string[] DocklyProcessNames = { "Dockly", "Dockly.Desktop" };

    private static string CommandsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Docklys", "commands");

    // Signals every currently running Dockly/Dockly.Desktop process to reload its module
    // registry and refresh the Settings module tab. Each command file is targeted at one PID
    // (Dockly's QueueCommand only claims a "reload-modules.<pid>.cmd" file whose pid matches its
    // own process) so N simultaneously running instances each reload exactly once, instead of
    // whichever instance's FileSystemWatcher happens to win the race to claim a single shared
    // file — which is what made multi-instance pushes deliver the module to the wrong window (or
    // none at all). Returns how many instances were signaled.
    public static int SignalReloadModules()
    {
        var pids = DocklyProcessNames
            .SelectMany(Process.GetProcessesByName)
            .Select(p => { try { return p.Id; } finally { p.Dispose(); } })
            .Distinct()
            .ToList();

        if (pids.Count == 0) return 0;

        try
        {
            Directory.CreateDirectory(CommandsDir);
            foreach (var pid in pids)
            {
                var file = Path.Combine(CommandsDir, $"reload-modules.{pid}.cmd");
                File.WriteAllText(file, "");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DocklyRemoteControl] SignalReloadModules failed: {ex.Message}");
            return 0;
        }
        return pids.Count;
    }

    // Developer opt-in markers Dockly's ModuleRegistry checks so a pushed dev DLL — unsigned,
    // and dropped straight into the "legacy" AppData Modules dir that a distributed Dockly build
    // refuses to load from — still loads on this machine. Only this dev tool ever creates them;
    // a distributed Dockly build never sees them and stays locked to signed registry modules.
    public static void EnsureDevModuleFlags()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Docklys");
            Directory.CreateDirectory(dir);
            foreach (var marker in new[] { "dev-allow-unsigned", "dev-allow-legacy-modules" })
            {
                var path = Path.Combine(dir, marker);
                if (!File.Exists(path)) File.WriteAllText(path, "");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DocklyRemoteControl] EnsureDevModuleFlags failed: {ex.Message}");
        }
    }
}

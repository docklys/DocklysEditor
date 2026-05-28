using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace RunModule;

// Editor-side mirror of Dockly's SkinService. Locates Dockly's loose Skins/
// folder via the same walk-up pattern RunModule already uses for
// CustomModules, loads any *.axaml as a Styles document, and swaps the
// active one inside Application.Current.Styles so the previewed module
// renders under the chosen skin.
public sealed class SkinHost
{
    public const string DefaultSkinName = "Default";

    private static readonly string PersistPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Docklys", "RunModule.json");

    private readonly string _skinsDir;
    private Styles? _activeSkin;
    private string? _activeSkinName;

    public string? ActiveSkinName => _activeSkinName;
    public string SkinsDirectory => _skinsDir;

    private SkinHost(string skinsDirectory) => _skinsDir = skinsDirectory;

    public static SkinHost? Create(string baseDir)
    {
        var dir = LocateSkinsDirectory(baseDir);
        return dir == null ? null : new SkinHost(dir);
    }

    public static string? LocateSkinsDirectory(string baseDir)
    {
        var dir = baseDir;
        while (!string.IsNullOrEmpty(dir))
        {
            foreach (var rel in new[]
                     {
                         Path.Combine("Skins"),
                         Path.Combine("Dockly", "Skins"),
                         Path.Combine("Docklys", "Dockly", "Skins"),
                         Path.Combine("Dockly", "Dockly", "Skins"),
                     })
            {
                var candidate = Path.Combine(dir, rel);
                if (HasSkinFiles(candidate)) return candidate;
            }
            var parent = Directory.GetParent(dir);
            if (parent == null) break;
            dir = parent.FullName;
        }

        // Fallback: a running Dockly process's output dir.
        try
        {
            var dockly = Process.GetProcessesByName("Dockly").FirstOrDefault()
                         ?? Process.GetProcessesByName("Dockly.Desktop").FirstOrDefault();
            var exe = dockly?.MainModule?.FileName;
            if (exe != null)
            {
                var nextToExe = Path.Combine(Path.GetDirectoryName(exe) ?? "", "Skins");
                if (HasSkinFiles(nextToExe)) return nextToExe;
            }
        }
        catch { }
        return null;
    }

    private static bool HasSkinFiles(string path)
    {
        try
        {
            return Directory.Exists(path) && Directory.EnumerateFiles(path, "*.axaml").Any();
        }
        catch { return false; }
    }

    public IReadOnlyList<string> ListSkins()
    {
        if (!Directory.Exists(_skinsDir)) return Array.Empty<string>();
        return Directory.EnumerateFiles(_skinsDir, "*.axaml")
                        .Select(Path.GetFileNameWithoutExtension)
                        .Where(n => !string.IsNullOrEmpty(n))
                        .Select(n => n!)
                        .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                        .ToList();
    }

    public string? ApplySkin(string? name)
    {
        var app = Application.Current;
        if (app == null) return null;

        var available = ListSkins();
        if (available.Count == 0) return null;

        var target = name;
        if (string.IsNullOrWhiteSpace(target)
            || !available.Any(s => string.Equals(s, target, StringComparison.OrdinalIgnoreCase)))
        {
            target = available.FirstOrDefault(s =>
                         string.Equals(s, DefaultSkinName, StringComparison.OrdinalIgnoreCase))
                     ?? available[0];
        }
        if (string.Equals(target, _activeSkinName, StringComparison.OrdinalIgnoreCase) && _activeSkin != null)
            return _activeSkinName;

        var path = Path.Combine(_skinsDir, target + ".axaml");
        Styles loaded;
        try
        {
            var xaml = File.ReadAllText(path);
            loaded = AvaloniaRuntimeXamlLoader.Parse<Styles>(xaml);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SkinHost] Failed to load skin '{target}' from {path}: {ex.Message}");
            return _activeSkinName;
        }

        if (_activeSkin != null)
        {
            try { app.Styles.Remove(_activeSkin); } catch { }
        }
        app.Styles.Add(loaded);
        _activeSkin = loaded;
        _activeSkinName = target;
        return _activeSkinName;
    }

    // Tiny JSON blob next to AppSettings so RunModule remembers cross-
    // session preferences independently of Dockly's main settings.
    internal sealed class PersistedState
    {
        public string? SkinName { get; set; }
        // Set by the Push-to-Docklys folder picker when the walk-up
        // search can't locate Dockly automatically. Remembering it means
        // the dev only picks once per machine.
        public string? DocklyInstallDir { get; set; }
        public Dictionary<string, string>? ThemeColors { get; set; }
    }

    // Load-modify-write helpers, so saving one field doesn't wipe the others.
    private static PersistedState LoadState()
    {
        try
        {
            if (!File.Exists(PersistPath)) return new PersistedState();
            var json = File.ReadAllText(PersistPath);
            return JsonSerializer.Deserialize<PersistedState>(json) ?? new PersistedState();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SkinHost] Failed to read {PersistPath}: {ex.Message}");
            return new PersistedState();
        }
    }

    public static Dictionary<string, string>? LoadThemeColors() => LoadState().ThemeColors;

    public static void SaveThemeColors(Dictionary<string, string> colors)
    {
        var s = LoadState();
        s.ThemeColors = colors;
        SaveState(s);
    }

    private static void SaveState(PersistedState state)
    {
        try
        {
            var dir = Path.GetDirectoryName(PersistPath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(PersistPath,
                JsonSerializer.Serialize(state,
                                         new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SkinHost] Failed to write {PersistPath}: {ex.Message}");
        }
    }

    public static string? LoadPersistedSkinName() => LoadState().SkinName;

    public static void SavePersistedSkinName(string name)
    {
        var s = LoadState();
        s.SkinName = name;
        SaveState(s);
    }

    public static string? LoadDocklyInstallDir() => LoadState().DocklyInstallDir;

    public static void SaveDocklyInstallDir(string? path)
    {
        var s = LoadState();
        s.DocklyInstallDir = path;
        SaveState(s);
    }
}

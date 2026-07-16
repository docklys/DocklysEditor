using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Controls.Primitives;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Rendering.SceneGraph;
using SkiaSharp;
using System.IO;
using Avalonia;
using System.Diagnostics;
using System;
using System.Linq;
using System.Threading.Tasks;
using Docklys.ModuleContracts;

namespace RunModule;

public partial class MainWindow : Window
{
    private string? _lastBuildError;

    // Every distinct exe path captured the last time KillDocklyProcesses ran, so "Start
    // Docklys" can relaunch every instance that was actually terminated — not just one.
    // Multiple Dockly processes can be running simultaneously (the single-instance guard in
    // Dockly/Program.cs is Linux-only), so a single scalar here silently dropped whichever
    // instance wasn't the first one enumerated.
    private readonly HashSet<string> _lastDocklyExePaths = new(StringComparer.OrdinalIgnoreCase);

    public MainWindow()
    {
        InitializeComponent();
        this.Loaded += MainWindow_Loaded;
    }

    private void MainWindow_Loaded(object? sender, RoutedEventArgs e)
    {
        InitializeTheme();
        InitializeSkinComboBox();
        InitializeDocklyLifecycleButton();
#if LINUX
        X11WebViewLayoutSync.Start(this);
#endif
        Console.WriteLine(typeof(IModule.FontDummy).Assembly.GetName().Name);

        // Ensure Zoom label shows current slider value on load. The actual
        // transform is applied later by ShowModuleAtIndex once the catalog
        // has loaded and the slot has its first module to scale.
        try
        {
            InitializeMainAppSizeSnaps();
            var zoom = this.FindControl<Slider>("ZoomSlider");
            if (zoom != null)
                UpdateZoomLabel(zoom.Value, null);
        }
        catch { }

        // Discover modules, fill the slot, and refresh arrow visibility.
        // Posted at Loaded priority so the window is fully measured first —
        // AutoSizeWindow needs the slot's measured size to do its job.
        Dispatcher.UIThread.Post(() =>
        {
            LoadCatalog();

            // Restore last selected module from persisted state
            var last = SkinHost.LoadLastSelectedModule();
            if (last != null)
            {
                var idx = _catalog.FindIndex(e =>
                    string.Equals(e.FolderName, last, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0) _currentIndex = idx;
            }

            ShowModuleAtIndex(_currentIndex);
            UpdateScrollArrowVisibility();
        }, DispatcherPriority.Loaded);
    }

    // Initial size + position come from the XAML (Width=1035, Height=450,
    // WindowStartupLocation=CenterScreen). This method's only job at
    // runtime is to *grow* the window when a cycled module or a zoomed
    // module would otherwise be clipped. It never repositions and never
    // shrinks — the user expects the window to stay where they put it.
    private void AutoSizeWindow()
    {
        var control = this.FindControl<Control>("ActiveModuleSlot");
        if (control == null) return;

        var screen = Screens.Primary;
        var workingArea = screen?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
        var maxWidth = workingArea.Width * 0.95;
        var maxHeight = workingArea.Height * 0.95;

        control.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var desiredSize = control.DesiredSize;

        // Two-row button area: ~28px × 2 rows + spacing + margins ≈ 78px.
        const int buttonHeight = 78;
        var requiredWidth = Math.Min(desiredSize.Width + 40, maxWidth);
        var requiredHeight = Math.Min(desiredSize.Height + buttonHeight + 70, maxHeight);

        if (requiredWidth > this.Width) this.Width = requiredWidth;
        if (requiredHeight > this.Height) this.Height = requiredHeight;
    }

    // Re-apply zoom to all visible module slots. Called by ShowModuleAtIndex
    // and by the slider handler so the window is always sized correctly.
    private void ApplyZoomToActiveModule(double percent)
    {
        var slot1 = this.FindControl<ContentControl>("ActiveModuleSlot");
        var slot2 = this.FindControl<ContentControl>("SecondModuleSlot");

        ApplyZoomToSlot(slot1, percent);
        if (_dualView && slot2?.IsVisible == true)
            ApplyZoomToSlot(slot2, percent);

        // Grow the window once with the combined footprint of all visible slots.
        double w = ValidDimension(slot1?.Width);
        if (_dualView && slot2?.IsVisible == true && !double.IsNaN(slot2.Width))
            w += 16 + ValidDimension(slot2.Width); // 16 px gap between the two instances
        GrowWindowToFitSlot(w, ValidDimension(slot1?.Height));
    }

    // Apply a zoom level to a single ContentControl slot without growing the window.
    private static void ApplyZoomToSlot(ContentControl? slot, double percent)
    {
        var control = slot?.Content as Control;
        if (slot == null || control == null) return;

        var scale = percent / 100.0;
        control.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        control.RenderTransform = new ScaleTransform(scale, scale);

        // RenderTransform scales the *visual* but not the layout slot — size
        // the slot to the scaled footprint so the content isn't clipped.
        control.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var desired = control.DesiredSize;
        if (desired.Width > 0 && desired.Height > 0)
        {
            slot.Width = desired.Width * scale;
            slot.Height = desired.Height * scale;
        }
    }

    private static double ValidDimension(double? v) =>
        v.HasValue && !double.IsNaN(v.Value) ? v.Value : 0;

    // Window grow helper used by zoom. Same chrome math as AutoSizeWindow,
    // but never repositions and never shrinks.
    private void GrowWindowToFitSlot(double slotWidth, double slotHeight)
    {
        if (double.IsNaN(slotWidth) || double.IsNaN(slotHeight)) return;
        var screen = Screens.Primary;
        var workingArea = screen?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
        var maxWidth = workingArea.Width * 0.95;
        var maxHeight = workingArea.Height * 0.95;
        const int buttonHeight = 78;

        var windowWidth = Math.Min(slotWidth + 40, maxWidth);
        var targetHeight = Math.Min(slotHeight + buttonHeight + 70, maxHeight);

        if (windowWidth > this.Width) this.Width = windowWidth;
        if (targetHeight > this.Height) this.Height = targetHeight;
    }
    private void OpenWebPFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_currentIndex >= 0 && _currentIndex < _catalog.Count)
        {
            var entry = _catalog[_currentIndex];
            var projectDir = Path.GetDirectoryName(entry.CsprojPath);
            if (projectDir != null)
            {
                var dir = Path.Combine(projectDir, "Images");
                Directory.CreateDirectory(dir);
                Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
                return;
            }
        }
        _ = ShowMessageDialog("Open folder", "No module selected.");
    }

    // Shared opener for the OutputWebPModule and OutputModuleDLL folders.
    // Both live at the editor solution root (next to DefaultModule.sln).
    private async Task OpenEditorOutputFolder(string subfolderName)
    {
        var editorRoot = FindEditorSolutionDir();
        if (editorRoot == null)
        {
            await ShowMessageDialog("Couldn't open folder",
                "The editor solution root (the folder containing DefaultModule.sln) " +
                "wasn't reachable by walking up from " + AppContext.BaseDirectory);
            return;
        }

        var outputDir = Path.Combine(editorRoot, subfolderName);
        Directory.CreateDirectory(outputDir);

        try
        {
            // UseShellExecute=true lets the OS pick the appropriate handler
            // (Explorer on Windows, Finder on macOS via `open`, xdg-open on Linux).
            Process.Start(new ProcessStartInfo
            {
                FileName = outputDir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            await ShowMessageDialog("Couldn't open folder",
                $"Failed to open {outputDir}\n\n{ex.GetType().Name}: {ex.Message}");
        }
    }

    private async void BuildAndDeploy_Click(object sender, RoutedEventArgs e)
    {
        var button = sender as Button;
        if (button == null) return;

        // Must match the button's Content in MainWindow.axaml — this is what the label resets to
        // once a transient state (Building… / ✓ Deployed / error) has run its course.
        const string originalContent = "⚡ Push to Docklys";

        // If in error state: copy log to clipboard, show confirmation, then rebuild.
        if (_lastBuildError != null)
        {
            var errorText = _lastBuildError;
            _lastBuildError = null;
            try
            {
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard != null) await clipboard.SetTextAsync(errorText);
            }
            catch { }
            button.Content = "✓ Copied! Rebuilding...";
            ToolTip.SetTip(button, null);
            await Task.Delay(700);
        }

        async Task ResetButton(int delayMs = 2500)
        {
            await Task.Delay(delayMs);
            if (_lastBuildError == null && button != null)
            {
                button.Content = originalContent;
                ToolTip.SetTip(button, null);
            }
        }

        void Fail(string shortLabel, string fullDetails)
        {
            Debug.WriteLine($"[Deploy] {shortLabel}:\n{fullDetails}");
            _lastBuildError = fullDetails;
            button.Content = $"✗ {shortLabel} — click to copy & retry";
            button.IsEnabled = true;
            ToolTip.SetTip(button, new TextBlock
            {
                Text = fullDetails,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 600,
                FontFamily = new FontFamily("Consolas,Courier New,monospace"),
                FontSize = 11,
            });
        }

        try
        {
            _lastBuildError = null;
            button.Content = "Building...";
            button.IsEnabled = false;
            ToolTip.SetTip(button, null);

            // Dev DLLs pushed by this tool are unsigned and land in directories Dockly's
            // ModuleRegistry otherwise refuses to load from in a distributed build (see
            // ModuleRegistry.IsUserManagedDirectory / ModuleSignature.Verify). Without these
            // markers a push can succeed (file copied) while Dockly silently rejects it, which
            // reads as "module doesn't show up in the tab" with no error anywhere.
            DocklyRemoteControl.EnsureDevModuleFlags();

            // Deploy the *active* module — whichever the carousel is on —
            // not a hardcoded VolumeMixer.
            if (_catalog.Count == 0)
            {
                Fail("No active module",
                    "No module is currently loaded. Use ✚ New to scaffold one before pushing.");
                return;
            }
            var activeEntry = _catalog[Math.Clamp(_currentIndex, 0, _catalog.Count - 1)];
            var proj = activeEntry.CsprojPath;
            var assemblyName = activeEntry.FolderName; // <Folder>/<Folder>.csproj → <Folder>.dll
            if (!File.Exists(proj))
            {
                Fail("Project not found",
                    $"Active module's csproj was on disk at scan time but is now missing:\n{proj}\n\n" +
                    "Click ↺ Reload Module to refresh the catalog.");
                return;
            }

            // Auto-discovery → saved path → folder picker. Picker
            // remembers its choice for next time so the dev only points
            // at the Dockly install folder once.
            var modulesDir = await ResolveDocklyModulesDirAsync(interactiveOnMiss: true);
            if (modulesDir == null)
            {
                Fail("Dockly not found",
                    "Could not locate Dockly's Modules folder.\n\n" +
                    $"Auto-search started from:\n{AppContext.BaseDirectory}\n\n" +
                    "Also probed running Dockly / Dockly.Desktop processes, " +
                    "and the saved install path in %APPDATA%/Docklys/RunModule.json. " +
                    "The folder picker was cancelled or pointed at the wrong folder.");
                return;
            }

            var projDir = Path.GetDirectoryName(proj) ?? "";

            // Find the freshest <Folder>.dll under OutputModuleDLL (preferred) or bin\Debug
            string? builtDll = null;
            var editorRoot = FindEditorSolutionDir();
            if (editorRoot != null)
            {
                var outputDir = Path.Combine(editorRoot, "OutputModuleDLL");
                if (Directory.Exists(outputDir))
                {
                    builtDll = Directory.GetFiles(outputDir, assemblyName + ".dll", SearchOption.AllDirectories)
                                        .OrderByDescending(File.GetLastWriteTimeUtc)
                                        .FirstOrDefault();
                }
            }
            
            // Fallback to project's bin directory if not in OutputModuleDLL
            if (builtDll == null)
            {
                var binDir = Path.Combine(projDir, "bin", "Debug");
                if (Directory.Exists(binDir))
                {
                    builtDll = Directory.GetFiles(binDir, assemblyName + ".dll", SearchOption.AllDirectories)
                                        .OrderByDescending(File.GetLastWriteTimeUtc)
                                        .FirstOrDefault();
                }
            }

            if (builtDll == null || !File.Exists(builtDll))
            {
                Fail("DLL not found",
                    $"No compiled {assemblyName}.dll found. Please build the project first.");
                return;
            }

            // Dockly scans *every* modules directory it can find. Sending one DLL to every
            // directory therefore registers the same module once per copy and creates duplicate
            // tiles in Settings. Keep exactly one authoritative deployment: the directory
            // ResolveDocklyModulesDirAsync selected. Before writing it, remove this module's old
            // copies from every other known directory so an earlier version of the editor cannot
            // leave duplicates behind.
            var staleCopyErrors = RemoveDuplicateDeployedModuleCopies(assemblyName, modulesDir);
            var targets = new List<string> { modulesDir };

            // A module's private NuGet dependencies must travel with it: Dockly loads modules
            // with Assembly.LoadFrom and rejects any whose non-framework references it can't
            // resolve, looking for them next to the module DLL. Shipping only <name>.dll is why
            // VolumeMixer was dropped with "unavailable runtime dependency -> NAudio.Wasapi".
            var deps = CollectModuleDependencies(projDir, assemblyName);

            var copied = new List<string>();
            var failures = new List<(string Dest, string Reason)>();
            var depFailures = new List<string>();
            foreach (var target in targets)
            {
                var dest = Path.Combine(target, assemblyName + ".dll");
                try
                {
                    Directory.CreateDirectory(target);
                    File.Copy(builtDll, dest, overwrite: true);
                    copied.Add(dest);
                    Debug.WriteLine($"[Deploy] Copied {builtDll} -> {dest}");

                    foreach (var dep in deps)
                    {
                        var depDest = Path.Combine(target, Path.GetFileName(dep));
                        try
                        {
                            File.Copy(dep, depDest, overwrite: true);
                        }
                        catch (Exception depEx)
                        {
                            // Non-fatal on its own: an existing copy that's merely locked still
                            // satisfies the load. Only a dependency that isn't there at all
                            // actually costs us the module, so surface just those.
                            if (!File.Exists(depDest))
                                depFailures.Add($"{depDest} — {depEx.GetType().Name}: {depEx.Message}");
                            Debug.WriteLine($"[Deploy] Dependency copy to {depDest} failed: {depEx.Message}");
                        }
                    }
                }
                catch (Exception copyEx)
                {
                    failures.Add((dest, $"{copyEx.GetType().Name}: {copyEx.Message}"));
                    Debug.WriteLine($"[Deploy] Copy to {dest} failed: {copyEx.Message}");
                }
            }

            // Zero successful copies while Dockly is running means the file-lock case is
            // overwhelmingly likely (Windows locks a loaded assembly's DLL). Point at the
            // Close button rather than silently retasking this one.
            if (copied.Count == 0 && IsDocklyRunning())
            {
                var lockReport = string.Join("\n  ",
                    failures.Select(f => f.Dest + "\n    " + f.Reason));
                Fail("Copy blocked — Docklys is running",
                    $"Docklys is running and is holding {assemblyName}.dll open in every target " +
                    "folder, so the new build could not be written.\n\n" +
                    "Click '■ Close Docklys', then push again.\n\nTargets:\n  " + lockReport);
                return;
            }

            if (copied.Count == 0)
            {
                var report = string.Join("\n  ",
                    failures.Select(f => f.Dest + "\n    " + f.Reason));
                Fail("Copy failed",
                    "Built DLL successfully but every copy target failed:\n  " + report +
                    "\n\nSource DLL: " + builtDll);
                return;
            }

            // Some-or-all-succeeded path. If Dockly is already running, signal every live
            // instance to hot-reload — Dockly never watches its Modules folders itself, so
            // without this the copied DLL sits on disk until the user manually restarts it.
            int reloadedInstances = IsDocklyRunning() ? DocklyRemoteControl.SignalReloadModules() : 0;

            var copiedReport = string.Join("\n  ", copied);
            button.Content = copied.Count == 1
                ? "✓ Deployed"
                : $"✓ Deployed ({copied.Count})";
            var tipMsg = $"Built: {builtDll}\n\nCopied to:\n  {copiedReport}";
            if (deps.Count > 0)
                tipMsg += "\n\nWith dependencies:\n  " + string.Join("\n  ", deps.Select(Path.GetFileName));
            if (reloadedInstances > 0)
                tipMsg += $"\n\nSignaled {reloadedInstances} running Dockly instance(s) to reload modules.";
            if (failures.Count > 0)
            {
                tipMsg += "\n\nFailed:\n  " + string.Join("\n  ",
                    failures.Select(f => f.Dest + " — " + f.Reason));
            }
            if (depFailures.Count > 0)
            {
                tipMsg += "\n\nMissing dependencies (Docklys will reject the module):\n  "
                          + string.Join("\n  ", depFailures);
            }
            if (staleCopyErrors.Count > 0)
            {
                tipMsg += "\n\nCould not remove stale duplicate copies:\n  "
                          + string.Join("\n  ", staleCopyErrors);
            }
            if (reloadedInstances == 0 && !IsDocklyRunning())
                tipMsg += "\n\nDocklys isn't running — click '▶ Start Docklys' to launch it.";
            ToolTip.SetTip(button, tipMsg);
            button.IsEnabled = true;
            await Task.Delay(1000);

            if (reloadedInstances > 0)
            {
                button.Content = reloadedInstances == 1
                    ? "✓ Reloaded"
                    : $"✓ Reloaded ({reloadedInstances})";
            }
            await ResetButton();
        }
        catch (Exception ex)
        {
            Fail("Unexpected error",
                $"{ex.GetType().Name}: {ex.Message}\n\n{ex.StackTrace}");
        }
        finally
        {
            // Only re-enable here when NOT in error state (Fail() handles its own re-enable).
            if (button != null && _lastBuildError == null)
                button.IsEnabled = true;
        }
    }

    // Auto-detect Dockly's Modules folder. Tries common layouts:
    //   <root>\Dockly\Modules
    //   <root>\Dockly\Dockly\Modules
    // Falls back to a running Dockly process's directory if nothing matches.
    private string? FindDocklyModulesDir()
    {
        // Try the standard AppData location first.
        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Docklys", "Modules");
        if (Directory.Exists(appData)) return appData;

        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            foreach (var rel in new[] {
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
                    Directory.CreateDirectory(m);
                    return m;
                }
            }
        }
        catch { }

        // Last sync fallback: the install dir the dev previously picked
        // via the folder picker (saved to %APPDATA%/Docklys/RunModule.json).
        // ResolveDocklyModulesDirAsync uses this too, but exposing
        // it here lets the sync "Start Docklys" code path benefit
        // without going through the async resolver.
        var saved = SkinHost.LoadDocklyInstallDir();
        var validated = ValidateAndResolveDocklyDir(saved);
        if (validated != null) return validated;

        return null;
    }

    private static readonly string[] DocklyProcessNames = { "Dockly", "Dockly.Desktop" };

    private static bool IsDocklyRunning()
    {
        foreach (var name in DocklyProcessNames)
        {
            var procs = Process.GetProcessesByName(name);
            try
            {
                if (procs.Length > 0) return true;
            }
            finally
            {
                foreach (var p in procs) p.Dispose();
            }
        }
        return false;
    }

    private void KillDocklyProcesses()
    {
        // Ground truth for this kill cycle — every path captured here is exactly what "Start
        // Docklys!" should relaunch, so start from empty rather than accumulating across cycles.
        lock (_lastDocklyExePaths) _lastDocklyExePaths.Clear();

        foreach (var name in DocklyProcessNames)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    // Capture every instance's exe path before kill (not just the first) so
                    // "Start Docklys!" can relaunch all of them.
                    var exe = p.MainModule?.FileName;
                    if (exe != null) lock (_lastDocklyExePaths) _lastDocklyExePaths.Add(exe);
                }
                catch { }
                try
                {
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(3000);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Deploy] Could not kill {name} (PID {p.Id}): {ex.Message}");
                }
                finally
                {
                    p.Dispose();
                }
            }
        }
    }

    private string? FindDocklyExecutable() => FindDocklyExecutableWithReport().exePath;

    // A Unix apphost is an extension-less ELF file with the execute bit; the managed .dll next to
    // it is neither. Used to tell them apart, since the Unix search has no ".exe" to filter on.
    private static bool IsExecutableFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists
                   && (info.UnixFileMode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute
                                            | UnixFileMode.OtherExecute)) != 0;
        }
        catch { return false; }
    }

    // Locates Dockly's executable across both published and dev layouts, and returns a
    // human-readable search report when nothing is found (used for the error tooltip).
    //
    // The install root is deliberately NOT derived from the Modules directory. Modules resolves
    // to %APPDATA%/Docklys/Modules first (it's the primary deploy target, and is created on
    // demand), whose parent is the AppData folder — that holds modules, logs and settings but
    // never the host, so anchoring the search there could only ever report "Could not locate
    // Dockly.Desktop.exe or Dockly.exe" no matter how many working builds existed on disk.
    private (string? exePath, string? searchReport) FindDocklyExecutableWithReport()
    {
        var searched = new List<string>();
        var found = new List<string>();

        // Only Windows builds carry the .exe suffix; on Linux/macOS the apphost is the bare
        // project name, so searching for ".exe" there can never match.
        var exeSuffix = OperatingSystem.IsWindows() ? ".exe" : "";
        var hostNames = new[] { "Dockly.Desktop", "Dockly" };

        void Consider(string path)
        {
            searched.Add(path);
            // The apphost has no extension on Unix, so a bare-name match can also hit non-exec
            // siblings of the managed <proj>.dll; keep only a real executable file.
            if (File.Exists(path) && (OperatingSystem.IsWindows() || IsExecutableFile(path)))
                found.Add(path);
        }

        // (1) A running instance knows exactly where it lives — the most reliable answer there is.
        foreach (var name in DocklyProcessNames)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    var exe = p.MainModule?.FileName;
                    if (exe != null) Consider(exe);
                }
                catch { }
                finally { p.Dispose(); }
            }
        }

        // (2) Probe each candidate root for the published layout (<root>/<host>) and the dev
        //     layout (<root>/<proj>/bin/<cfg>/<tfm>/<host>).
        foreach (var root in CandidateDocklyRoots())
        {
            foreach (var host in hostNames)
                Consider(Path.Combine(root, host + exeSuffix));

            foreach (var proj in hostNames)
            {
                var binDir = Path.Combine(root, proj, "bin");
                if (!Directory.Exists(binDir))
                {
                    searched.Add(Path.Combine(binDir, "**", proj + exeSuffix));
                    continue;
                }
                try
                {
                    foreach (var candidate in Directory.GetFiles(binDir, proj + exeSuffix, SearchOption.AllDirectories))
                        Consider(candidate);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Deploy] Search under {binDir} failed: {ex.Message}");
                }
            }
        }

        if (found.Count > 0)
            return (found.OrderByDescending(File.GetLastWriteTimeUtc).First(), null);

        var report =
            $"Could not locate the Docklys executable ({string.Join(" or ", hostNames.Select(h => h + exeSuffix))}).\n\n" +
            "Searched:\n - " + string.Join("\n - ", searched.Distinct(StringComparer.OrdinalIgnoreCase)) +
            "\n\nBuild Docklys (dotnet build Dockly.Desktop/Dockly.Desktop.csproj), or use " +
            "'📁 Docklys Modules' and pick your Docklys install folder so its location is remembered.";
        return (null, report);
    }

    // Roots that may contain the Docklys host, most specific first.
    private List<string> CandidateDocklyRoots()
    {
        var roots = new List<string>();
        void Add(string? p)
        {
            if (string.IsNullOrWhiteSpace(p)) return;
            try
            {
                var full = NormalizeDir(p);
                if (Directory.Exists(full)
                    && !IsAppDataDocklysRoot(full)
                    && !roots.Contains(full, StringComparer.OrdinalIgnoreCase))
                    roots.Add(full);
            }
            catch { /* bad path — skip */ }
        }

        // The install folder the dev previously pointed the picker at.
        Add(SkinHost.LoadDocklyInstallDir());

        // Walk up from this editor's output dir. In a dev checkout Docklys is a sibling repo
        // (…/Docklys/DocklysEditor next to …/Docklys/Dockly), so each level and its Dockly
        // child are both worth probing.
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            Add(dir);
            Add(Path.Combine(dir, "Dockly"));
            Add(Path.Combine(dir, "Docklys", "Dockly"));
            var parent = Directory.GetParent(dir);
            if (parent == null) break;
            dir = parent.FullName;
        }

        // Install roots implied by the Modules dirs we can see — a published layout keeps the
        // host next to Modules. Add() filters out the AppData one, which never holds a host.
        foreach (var modulesDir in FindAllDocklyModulesDirs())
            Add(Path.GetDirectoryName(modulesDir));

        return roots;
    }

    // Absolute path with any trailing separator removed, so the same directory reached via
    // different spellings ("…/net10.0/" vs "…/net10.0") compares and de-duplicates as one.
    // TrimEndingDirectorySeparator leaves root paths ("/", "C:\") intact.
    private static string NormalizeDir(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    // %APPDATA%/Docklys (and its LocalAppData twin) hold Modules, logs, settings and profiles —
    // never the host — so they must never be treated as an install root.
    private static bool IsAppDataDocklysRoot(string dir)
    {
        foreach (var special in new[] { Environment.SpecialFolder.ApplicationData,
                                        Environment.SpecialFolder.LocalApplicationData })
        {
            try
            {
                var appDataRoot = Path.Combine(Environment.GetFolderPath(special), "Docklys");
                if (string.Equals(NormalizeDir(dir), NormalizeDir(appDataRoot), StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch { }
        }
        return false;
    }

    private async void ReloadModule_Click(object sender, RoutedEventArgs e)
    {
        if (_catalog.Count == 0) return;
        var oldEntry = _catalog[_currentIndex];
        var projFolder = Path.GetDirectoryName(oldEntry.CsprojPath) ?? "";

        // Drop all slot contents so module instances are detached before the
        // old isolated context is unloaded — avoids use-after-free on module types.
        ClearModuleSlots();
        await Task.Delay(150);

        // Pick up the freshest DLL from disk (reflects any recent dotnet build).
        var freshDll = FindFreshestModuleDll(projFolder, oldEntry.FolderName) ?? oldEntry.DllPath;

        // Unload the old isolated context; the GC can now reclaim the old types.
        TryUnloadContext(oldEntry.LoadContext);

        ModuleLoadContext? newCtx = null;
        Type? newType = null;
        try
        {
            newCtx = new ModuleLoadContext(freshDll);
            var asm = newCtx.LoadModule();
            newType = SafeGetTypes(asm).FirstOrDefault(IsModuleType);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Reload] Load failed for {freshDll}: {ex.Message}");
            TryUnloadContext(newCtx);
            newCtx = null;
        }

        if (newType != null && newCtx != null)
        {
            _catalog[_currentIndex] = oldEntry with { DllPath = freshDll, ModuleType = newType, LoadContext = newCtx };
        }
        else
        {
            // Fallback: re-load from the last known DLL path so the slot isn't blank.
            ModuleLoadContext? fallbackCtx = null;
            try
            {
                fallbackCtx = new ModuleLoadContext(oldEntry.DllPath);
                var fbAsm = fallbackCtx.LoadModule();
                var fbType = SafeGetTypes(fbAsm).FirstOrDefault(IsModuleType);
                if (fbType != null)
                    _catalog[_currentIndex] = oldEntry with { ModuleType = fbType, LoadContext = fallbackCtx };
                else
                    TryUnloadContext(fallbackCtx);
            }
            catch { TryUnloadContext(fallbackCtx); }
        }

        ShowModuleAtIndex(_currentIndex);
    }

    private void ToggleDualView_Click(object sender, RoutedEventArgs e)
    {
        var btn = sender as Avalonia.Controls.Primitives.ToggleButton;
        _dualView = btn?.IsChecked == true;
        ShowModuleAtIndex(_currentIndex);
    }

    private void OpenLegalLink_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem && menuItem.Tag is string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Legal] Failed to open link {url}: {ex.Message}");
            }
        }
    }

    private async void OpenSavesFolder_Click(object sender, RoutedEventArgs e)
    {
        var savesDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Docklys", "ModuleSaves");
        Directory.CreateDirectory(savesDir);
        try
        {
            Process.Start(new ProcessStartInfo { FileName = savesDir, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            await ShowMessageDialog("Couldn't open folder",
                $"Failed to open {savesDir}\n\n{ex.GetType().Name}: {ex.Message}");
        }
    }


    private bool _suppressSkinSelectionChange;

    private void InitializeSkinComboBox()
    {
        var combo = this.FindControl<ComboBox>("SkinComboBox");
        if (combo == null) return;

        var skins = App.Skins;
        if (skins == null)
        {
            combo.IsEnabled = false;
            combo.PlaceholderText = "No Dockly\\Skins folder found";
            return;
        }

        var list = skins.ListSkins();
        _suppressSkinSelectionChange = true;
        try
        {
            combo.ItemsSource = list;
            combo.SelectedItem = skins.ActiveSkinName;
        }
        finally
        {
            _suppressSkinSelectionChange = false;
        }
    }

    private void SkinComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressSkinSelectionChange) return;
        if (sender is not ComboBox combo) return;
        if (combo.SelectedItem is not string name) return;

        var skins = App.Skins;
        if (skins == null) return;

        var applied = skins.ApplySkin(name);
        if (applied != null)
            SkinHost.SavePersistedSkinName(applied);
    }

    private async void OpenOutputModuleDllFolder_Click(object sender, RoutedEventArgs e)
    {
        await OpenEditorOutputFolder("OutputModuleDLL");
    }

    private async void OpenDocklyModulesFolder_Click(object sender, RoutedEventArgs e)
    {
        var path = await ResolveDocklyModulesDirAsync(interactiveOnMiss: true);
        if (path == null) return;
        Directory.CreateDirectory(path);
        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            await ShowMessageDialog("Couldn't open folder", ex.Message);
        }
    }

    /// <summary>
    /// Uses reflection to reach into the Avalonia.WebView.Desktop implementation and
    /// request a native CapturePreviewAsync from the underlying CoreWebView2.
    /// Returns an SKBitmap containing the rendered web content.
    /// </summary>
    private async Task<SKBitmap?> CaptureWebViewAsync(Control webView)
    {
        try
        {
            // AvaloniaWebView on Windows uses a private _platformWebView field to hold its Win32 host
            var platformField = webView.GetType().GetField("_platformWebView", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var platform = platformField?.GetValue(webView);
            if (platform == null) return null;

            // The platform-specific host has a _coreWebView2Controller property
            var controllerProp = platform.GetType().GetProperty("_coreWebView2Controller", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var controller = controllerProp?.GetValue(platform);
            if (controller == null) return null;

            // The controller exposes the CoreWebView2 engine
            var coreProp = controller.GetType().GetProperty("CoreWebView2");
            var core = coreProp?.GetValue(controller);
            if (core == null) return null;

            // Call CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream)
            var captureMethod = core.GetType().GetMethod("CapturePreviewAsync");
            if (captureMethod == null) return null;

            using var ms = new MemoryStream();
            // CoreWebView2CapturePreviewImageFormat.Png is 0
            var task = (Task)captureMethod.Invoke(core, new object[] { 0, ms })!;
            if (task == null) return null;
            
            await task;

            ms.Seek(0, SeekOrigin.Begin);
            return SKBitmap.Decode(ms);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RunModule] WebView capture failed: {ex.Message}");
            return null;
        }
    }
}

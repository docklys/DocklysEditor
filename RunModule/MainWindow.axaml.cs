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
    private bool _docklyLocked;
    private bool _canStartDockly;
    private string? _lastDocklyExePath;

    public MainWindow()
    {
        InitializeComponent();
        this.Loaded += MainWindow_Loaded;
    }

    private void MainWindow_Loaded(object? sender, RoutedEventArgs e)
    {
        InitializeTheme();
        InitializeSkinComboBox();
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

    private async void CaptureToWebP_Click(object sender, RoutedEventArgs e)
    {
        // WebP previews are per-module — capture the inner content of the
        // active slot at 100%. The ContentControl wrapper would add
        // alignment chrome to the bounding box, so reach through to its
        // Content.
        var slot = this.FindControl<ContentControl>("ActiveModuleSlot");
        var control = slot?.Content as Control;
        if (control == null) return;

            // Capture at 100% scale without moving the visible module permanently.
            // Supersampling scale: increase to render at higher resolution for much better saved image quality.
            // Typical values: 2 or 4. 4 gives much sharper results but uses more memory.
            const int captureScale = 4; // change to 2 or 1 if you need smaller files
        var originalBounds = control.Bounds;
        var originalTransform = control.RenderTransform;
        var originalTransformOrigin = control.RenderTransformOrigin;

        // Force capture at 100% (regardless of current zoom) and render off at origin, then restore layout.
        try
        {
            control.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
            control.RenderTransform = new ScaleTransform(1.0, 1.0);

            // Measure for desired size and create bitmap accordingly (capture full module content at 100%)
            control.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var desired = control.DesiredSize;
            // Multiply desired size and DPI by captureScale to supersample the render.
            var size = new PixelSize(Math.Max(1, (int)Math.Ceiling(desired.Width * captureScale)), Math.Max(1, (int)Math.Ceiling(desired.Height * captureScale)));
            var dpi = new Vector(96 * captureScale, 96 * captureScale);

            using var rtb = new RenderTargetBitmap(size, dpi);

            // Temporarily arrange at origin for rendering, then restore below
            control.Arrange(new Rect(0, 0, desired.Width, desired.Height));
            rtb.Render(control);

            using var stream = new MemoryStream();
            rtb.Save(stream);
            stream.Seek(0, SeekOrigin.Begin);

            // Load the Avalonia-rendered part into a mutable SkiaSharp bitmap
            using var skStream = new SKManagedStream(stream);
            var decoded = SKBitmap.Decode(skStream);
            if (decoded == null)
            {
                Debug.WriteLine("❌ Decoded Avalonia SKBitmap is null");
                return;
            }
            
            // Create the final bitmap (mutable) and draw the Avalonia part as the base layer
            var skBitmap = new SKBitmap(decoded.Width, decoded.Height);
            using (var canvas = new SKCanvas(skBitmap))
            {
                canvas.DrawBitmap(decoded, 0, 0);
                decoded.Dispose();

                // --- BEGIN WEBVIEW COMPOSITING ---
                // Native HWNDs (like WebView) are invisible to RenderTargetBitmap.
                // We find them in the visual tree and "stamp" their live content on top.
                var webViews = control.GetVisualDescendants()
                    .OfType<Control>()
                    .Where(c => c.GetType().Name.Contains("WebView"))
                    .ToList();

                if (webViews.Any())
                {
                    Debug.WriteLine($"[RunModule] Compositing {webViews.Count} WebView(s) into screenshot...");
                    
                    // The native WebView HWND is manipulated by the modules to hide scrollbars
                    // and leave a padding for the rounded border. We must replicate this visually.
                    const double WebViewPadding = 6.0;
                    const double ScrollbarExtra = 15.0;

                    foreach (var wv in webViews)
                    {
                        var wvBitmap = await CaptureWebViewAsync(wv);
                        if (wvBitmap == null)
                        {
                            Debug.WriteLine($"[RunModule] Failed to capture WebView: {wv.GetType().Name}");
                            continue;
                        }

                        // Determine where the WebView sits relative to the module's top-left
                        var transform = wv.TransformToVisual(control);
                        if (transform.HasValue)
                        {
                            var rect = new Rect(wv.Bounds.Size).TransformToAABB(transform.Value);
                            
                            // The actual visible area inside the padding
                            var visibleRect = new SKRect(
                                (float)((rect.X + WebViewPadding) * captureScale),
                                (float)((rect.Y + WebViewPadding) * captureScale),
                                (float)((rect.Right - WebViewPadding) * captureScale),
                                (float)((rect.Bottom - WebViewPadding) * captureScale)
                            );

                            // Calculate the crop source rect. The native wvBitmap includes
                            // the ScrollbarExtra. We only want the visible portion.
                            double logicalVisibleW = rect.Width - (WebViewPadding * 2);
                            double logicalVisibleH = rect.Height - (WebViewPadding * 2);
                            
                            float srcVisibleW = wvBitmap.Width * (float)(logicalVisibleW / (logicalVisibleW + ScrollbarExtra));
                            float srcVisibleH = wvBitmap.Height * (float)(logicalVisibleH / (logicalVisibleH + ScrollbarExtra));
                            var srcRect = new SKRect(0, 0, srcVisibleW, srcVisibleH);
                            
                            // Apply rounded corners (module has CornerRadius 10, minus 6 padding = 4)
                            using var path = new SKPath();
                            float cornerRadius = 4f * captureScale;
                            path.AddRoundRect(visibleRect, cornerRadius, cornerRadius);
                            
                            canvas.Save();
                            canvas.ClipPath(path, SKClipOperation.Intersect, antialias: true);
                            
                            // Draw the WebView's pixels
                            canvas.DrawBitmap(wvBitmap, srcRect, visibleRect);
                            
                            canvas.Restore();
                            
                            Debug.WriteLine($"[RunModule] ✓ Composited WebView at {visibleRect.Left:0},{visibleRect.Top:0}");
                        }
                        wvBitmap.Dispose();
                    }
                }
                // --- END WEBVIEW COMPOSITING ---
            }

            // The 'skBitmap' now contains "both together": the Avalonia UI + the WebViews.
            SKBitmap bitmapToSave = skBitmap;

            // Proceed to saving below (history logic)

            // Locate the editor solution root via the .sln file — was
            // previously walking up looking for a folder literally named
            // "DocklysEditor", which never matched (the actual folder is
            // "DocklysModuleEditor") so the capture silently dropped on
            // the floor.
            var editorRoot = FindEditorSolutionDir();
            if (editorRoot == null)
            {
                Debug.WriteLine($"❌ Could not locate the editor solution root (no DefaultModule.sln) from: {AppContext.BaseDirectory}");
                return;
            }

            var outputDir = Path.Combine(editorRoot, "OutputWebPModule");
            Directory.CreateDirectory(outputDir);

            // Rotating history: ModulePreview1.webp .. ModulePreview5.webp
            var historyFiles = new string[5];
            for (int i = 0; i < 5; i++)
                historyFiles[i] = Path.Combine(outputDir, $"ModulePreview{i + 1}.webp");

            // If there are existing files, shift older down (1 <- 2, 2 <- 3, ..., 4 <- 5)
            if (File.Exists(historyFiles[0]))
                File.Delete(historyFiles[0]);

            for (int i = 0; i < 4; i++)
            {
                if (File.Exists(historyFiles[i + 1]))
                {
                    if (File.Exists(historyFiles[i]))
                        File.Delete(historyFiles[i]);
                    File.Move(historyFiles[i + 1], historyFiles[i]);
                }
            }

            // Save the new capture as latest (index 4)
            var savePath = historyFiles[4];
            using (var output = File.OpenWrite(savePath))
            using (var skImage = SKImage.FromBitmap(bitmapToSave))
            // Use highest quality (100) for WebP encoding (lossy but max quality supported by this API)
            using (var skData = skImage.Encode(SKEncodedImageFormat.Webp, 100))
            {
                skData.SaveTo(output);
            }

            if (File.Exists(savePath))
            {
                var fileInfo = new FileInfo(savePath);
                Debug.WriteLine($"✓ WebP file created: {savePath} ({fileInfo.Length} bytes)");
            }
            else
            {
                Debug.WriteLine("❌ Failed to create WebP file");
            }

            return; // normal exit (restoration in finally)
        }
        finally
        {
            // Restore the control's transform and layout so it stays visually centered
            try
            {
                control.RenderTransform = originalTransform;
                control.RenderTransformOrigin = originalTransformOrigin;
                control.Arrange(originalBounds);
            }
            catch { }
        }

        // (capture and save logic handled above; nothing further required here)
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
    private async void OpenWebPFolder_Click(object sender, RoutedEventArgs e)
    {
        await OpenEditorOutputFolder("OutputWebPModule");
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

        const string originalContent = "Push to Docklys!";

        // If we just deployed and Dockly is offline: launch it, then reset to original.
        if (_canStartDockly)
        {
            _canStartDockly = false;
            button.IsEnabled = false;
            button.Content = "Starting Docklys...";
            ToolTip.SetTip(button, null);

            string? exe = _lastDocklyExePath;
            string? searchReport = null;
            if (exe == null || !File.Exists(exe))
            {
                var found = FindDocklyExecutableWithReport();
                exe = found.exePath;
                searchReport = found.searchReport;
            }

            string? startError = null;
            bool started = false;
            if (exe != null && File.Exists(exe))
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = exe,
                        UseShellExecute = true,
                        WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
                    });
                    started = true;
                    _lastDocklyExePath = exe;
                }
                catch (Exception startEx)
                {
                    startError =
                        $"Failed to launch Docklys.\n\nExe: {exe}\n\n" +
                        $"{startEx.GetType().Name}: {startEx.Message}\n\n{startEx.StackTrace}";
                    Debug.WriteLine($"[Deploy] Start Docklys failed: {startEx}");
                }
            }
            else
            {
                startError = searchReport ?? "Could not locate Dockly.Desktop.exe or Dockly.exe.";
            }

            if (started)
            {
                button.Content = "✓ Started";
                ToolTip.SetTip(button, $"Launched: {exe}");
                button.IsEnabled = true;
                await Task.Delay(1500);
                if (_lastBuildError == null && !_canStartDockly && !_docklyLocked)
                {
                    button.Content = originalContent;
                    ToolTip.SetTip(button, null);
                }
            }
            else
            {
                // Persist the error so the user can hover to read it and click to copy + retry.
                Fail("Start failed", startError ?? "Unknown error launching Docklys.");
            }
            return;
        }

        // If Dockly is holding the DLL open: terminate it, then fall through to retry.
        if (_docklyLocked)
        {
            _docklyLocked = false;
            _lastBuildError = null;
            button.IsEnabled = false;
            button.Content = "Closing Docklys...";
            ToolTip.SetTip(button, null);
            try
            {
                await Task.Run(KillDocklyProcesses);
            }
            catch (Exception killEx)
            {
                Debug.WriteLine($"[Deploy] Kill Docklys failed: {killEx}");
            }
            // Give the OS a moment to release the file handle on the DLL.
            await Task.Delay(500);
            button.IsEnabled = true;
            // fall through to the normal build+copy flow below
        }

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

            // Copy to EVERY Dockly Modules dir we can locate, not
            // just one. Dockly's ModuleRegistry resolves the dir in
            // priority order at startup (env var → BaseDirectory/Modules →
            // assembly-location/Modules → walk-up → LocalAppData),
            // so a Dockly that's been run once and a Dockly that's never
            // been run end up reading from different folders. Copying to
            // all of them is the only reliable way to make sure the
            // module shows up regardless of state.
            var targets = FindAllDocklyModulesDirs();
            if (!targets.Contains(modulesDir, StringComparer.OrdinalIgnoreCase))
                targets.Add(modulesDir);
            if (targets.Count == 0) targets.Add(modulesDir);

            var copied = new List<string>();
            var failures = new List<(string Dest, string Reason)>();
            foreach (var target in targets)
            {
                var dest = Path.Combine(target, assemblyName + ".dll");
                try
                {
                    Directory.CreateDirectory(target);
                    File.Copy(builtDll, dest, overwrite: true);
                    copied.Add(dest);
                    Debug.WriteLine($"[Deploy] Copied {builtDll} -> {dest}");
                }
                catch (Exception copyEx)
                {
                    failures.Add((dest, $"{copyEx.GetType().Name}: {copyEx.Message}"));
                    Debug.WriteLine($"[Deploy] Copy to {dest} failed: {copyEx.Message}");
                }
            }

            // If we got zero successful copies and Dockly is running, the
            // file-lock case is overwhelmingly likely — offer the same
            // close-and-retry UX the original single-target path had.
            if (copied.Count == 0 && IsDocklyRunning())
            {
                _docklyLocked = true;
                _lastBuildError = null;
                button.Content = "Close Docklys!";
                button.IsEnabled = true;
                ToolTip.SetTip(button,
                    $"Dockly is running and is holding {assemblyName}.dll open in every target folder.\n" +
                    "Click to terminate Dockly and retry the push.");
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

            // Some-or-all-succeeded path.
            var copiedReport = string.Join("\n  ", copied);
            button.Content = copied.Count == 1
                ? "✓ Deployed"
                : $"✓ Deployed ({copied.Count})";
            var tipMsg = $"Built: {builtDll}\n\nCopied to:\n  {copiedReport}";
            if (failures.Count > 0)
            {
                tipMsg += "\n\nFailed:\n  " + string.Join("\n  ",
                    failures.Select(f => f.Dest + " — " + f.Reason));
            }
            ToolTip.SetTip(button, tipMsg);
            button.IsEnabled = true;
            await Task.Delay(1000);

            if (_lastBuildError == null && !IsDocklyRunning())
            {
                _canStartDockly = true;
                button.Content = "Start Docklys!";
                ToolTip.SetTip(button, "Click to launch Dockly.");
            }
            else
            {
                await ResetButton(0);
            }
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
        foreach (var name in DocklyProcessNames)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    // Capture exe path before kill so "Start Docklys!" can relaunch it.
                    try { _lastDocklyExePath ??= p.MainModule?.FileName; } catch { }
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

    // Locates Dockly's exe across both published and dev layouts, and returns a
    // human-readable search report when nothing is found (used for the error tooltip).
    private (string? exePath, string? searchReport) FindDocklyExecutableWithReport()
    {
        var modulesDir = FindDocklyModulesDir();
        if (modulesDir == null)
            return (null, "Could not locate the Dockly\\Modules directory. Build/install Dockly so its folder is reachable from this project tree.");

        var docklyDir = Path.GetDirectoryName(modulesDir);
        if (docklyDir == null)
            return (null, $"Could not determine Dockly root from:\n{modulesDir}");

        var searched = new System.Collections.Generic.List<string>();

        // 1. Published / release layout: exe sits next to Modules.
        foreach (var name in new[] { "Dockly.Desktop.exe", "Dockly.exe" })
        {
            var path = Path.Combine(docklyDir, name);
            searched.Add(path);
            if (File.Exists(path)) return (path, null);
        }

        // 2. Dev layout: <root>\<Proj>\bin\<Config>\<TFM>\<Proj>.exe — pick the freshest.
        foreach (var projDir in new[] { "Dockly.Desktop", "Dockly" })
        {
            var binDir = Path.Combine(docklyDir, projDir, "bin");
            searched.Add(Path.Combine(binDir, "**", $"{projDir}.exe"));
            if (!Directory.Exists(binDir)) continue;
            try
            {
                var freshest = Directory.GetFiles(binDir, $"{projDir}.exe", SearchOption.AllDirectories)
                                        .OrderByDescending(File.GetLastWriteTimeUtc)
                                        .FirstOrDefault();
                if (freshest != null) return (freshest, null);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Deploy] Search under {binDir} failed: {ex.Message}");
            }
        }

        var report =
            "Could not locate Dockly.Desktop.exe or Dockly.exe.\n\n" +
            "Searched:\n - " + string.Join("\n - ", searched) +
            "\n\nBuild the Dockly.Desktop project (dotnet build in " +
            Path.Combine(docklyDir, "Dockly.Desktop") + ") and try again.";
        return (null, report);
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
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Controls.Primitives;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
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

    public MainWindow()
    {
        InitializeComponent();
        this.Loaded += MainWindow_Loaded;
    }

    private void MainWindow_Loaded(object? sender, RoutedEventArgs e)
    {
        // Auto-size window to fit module content
        AutoSizeWindow();
        Console.WriteLine(typeof(IModule.FontDummy).Assembly.GetName().Name);

        // Ensure Zoom label shows current slider value on load and apply initial scale
        try
        {
            var zoom = this.FindControl<Slider>("ZoomSlider");
            var lbl = this.FindControl<TextBlock>("ZoomLabel");
            if (zoom != null)
            {
                var v = zoom.Value;
                if (lbl != null)
                    lbl.Text = $"{(int)v}%";

                var control = this.FindControl<Control>("ModuleControl");
                if (control != null)
                {
                    var scale = v / 100.0;
                    control.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
                    control.RenderTransform = new ScaleTransform(scale, scale);
                }
            }
        }
        catch { }
    }

    private void AutoSizeWindow()
    {
        var control = this.FindControl<Control>("ModuleControl");
        if (control == null) return;

        // Get screen dimensions
        var screen = Screens.Primary;
        var workingArea = screen?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
        var maxWidth = workingArea.Width * 0.9; // 90% of screen width
        var maxHeight = workingArea.Height * 0.9; // 90% of screen height

        // Force measure the control to get its desired size
        control.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var desiredSize = control.DesiredSize;

        // Two-row button area: ~28px × 2 rows + 6px spacing + 16px margins ≈ 78px
        var buttonHeight = 78;

        // Calculate window size based on module content
        var windowWidth = Math.Min(Math.Max(desiredSize.Width + 40, 400), maxWidth); // Min 400px, max 90% screen
        var windowHeight = Math.Min(Math.Max(desiredSize.Height + buttonHeight + 40, 300), maxHeight); // Min 300px, max 90% screen

        // Set window size
        this.Width = windowWidth;
        this.Height = windowHeight + 30; // Add extra space for compact control row and margins
        
        // Position window in center-right of screen
        var centerX = workingArea.X + workingArea.Width - windowWidth - 20;
        var centerY = workingArea.Y + (workingArea.Height / 2) - ((windowHeight + 50) / 2) - 15; // Center vertically -15 for Header
        
        this.Position = new PixelPoint((int)centerX, (int)centerY);
        
        // Set minimum size to ensure usability
        this.MinWidth = Math.Min(400, windowWidth);
        this.MinHeight = Math.Min(300, windowHeight);

        Debug.WriteLine($"Module desired size: {desiredSize.Width} x {desiredSize.Height}");
        Debug.WriteLine($"Window size set to: {windowWidth} x {windowHeight + 50}");
        Debug.WriteLine($"Window position set to: {centerX} x {centerY}");
        Debug.WriteLine($"Screen working area: {workingArea.Width} x {workingArea.Height}");
    }

    private void CaptureToWebP_Click(object sender, RoutedEventArgs e)
    {
        var control = this.FindControl<Control>("ModuleControl");
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

            // Load into SkiaSharp bitmap
            using var skStream = new SKManagedStream(stream);
            using var skBitmap = SKBitmap.Decode(skStream);
            if (skBitmap == null)
            {
                Debug.WriteLine("❌ Decoded SKBitmap is null");
                return;
            }

            // We captured at 100% so no additional visual scaling is required
            SKBitmap bitmapToSave = skBitmap;

            // Proceed to saving below (history logic)

            // Save as WebP — walk up from the exe's directory to find DocklysEditor root
            var searchDir = AppContext.BaseDirectory;
            string? editorRoot = null;

            while (searchDir != null && !Path.GetFileName(searchDir).Equals("DocklysEditor", StringComparison.OrdinalIgnoreCase))
            {
                searchDir = Directory.GetParent(searchDir)?.FullName;
            }

            if (searchDir != null)
                editorRoot = searchDir;

            if (editorRoot == null)
            {
                Debug.WriteLine($"❌ Could not locate DocklysEditor root from: {AppContext.BaseDirectory}");
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

    private void ZoomSlider_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        try
        {
            var lbl = this.FindControl<TextBlock>("ZoomLabel");
            var control = this.FindControl<Control>("ModuleControl");
            if (lbl != null)
                lbl.Text = $"{(int)e.NewValue}%";

            if (control != null)
            {
                var scale = e.NewValue / 100.0;
                control.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
                control.RenderTransform = new ScaleTransform(scale, scale);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Zoom change failed: {ex}");
        }
    }
    private void OpenWebPFolder_Click(object sender, RoutedEventArgs e)
    {
        // Locate DocklysEditor root
        var searchDir = AppContext.BaseDirectory;
        string? editorRoot = null;
        while (searchDir != null && !Path.GetFileName(searchDir).Equals("DocklysEditor", StringComparison.OrdinalIgnoreCase))
        {
            searchDir = Directory.GetParent(searchDir)?.FullName;
        }

        if (searchDir != null)
            editorRoot = searchDir;

        if (editorRoot == null)
        {
            Debug.WriteLine("Could not locate DocklysEditor root to open WebP folder");
            return;
        }

        var outputDir = Path.Combine(editorRoot, "OutputWebPModule");
        Directory.CreateDirectory(outputDir);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{outputDir}\"",
                UseShellExecute = true,
            };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to open Explorer: {ex}");
        }
    }

    private async void BuildAndDeploy_Click(object sender, RoutedEventArgs e)
    {
        var button = sender as Button;
        if (button == null) return;

        const string originalContent = "Push to Dockly!";

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

            var proj = FindVolumeMixerProject();
            if (proj == null)
            {
                Fail("Project not found",
                    $"Could not locate Docklys.VolumeMixer\\VolumeMixer.csproj by walking up from:\n{AppContext.BaseDirectory}");
                return;
            }

            var customModulesDir = FindDocklyCustomModulesDir();
            if (customModulesDir == null)
            {
                Fail("Dockly not found",
                    $"Could not locate Dockly\\CustomModules by walking up from:\n{AppContext.BaseDirectory}\n\nAlso searched running processes named 'Dockly' / 'Dockly.Desktop'.");
                return;
            }

            var projDir = Path.GetDirectoryName(proj) ?? "";

            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"build \"{proj}\" -c Debug -nologo -v minimal",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = projDir,
            };

            Process? process;
            try { process = Process.Start(psi); }
            catch (Exception startEx)
            {
                Fail("dotnet failed to start",
                    $"Could not launch the dotnet CLI.\n\n{startEx.GetType().Name}: {startEx.Message}\n\nMake sure the .NET SDK is installed and 'dotnet' is on PATH.");
                return;
            }
            if (process == null)
            {
                Fail("dotnet not found",
                    "Process.Start returned null. The .NET SDK may not be installed or 'dotnet' is not on PATH.");
                return;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode != 0)
            {
                var details =
                    $"dotnet build exited with code {process.ExitCode}\n" +
                    $"Project: {proj}\n" +
                    $"Working dir: {projDir}\n" +
                    "\n--- stdout ---\n" +
                    (string.IsNullOrWhiteSpace(stdout) ? "(empty)" : stdout.Trim()) +
                    "\n\n--- stderr ---\n" +
                    (string.IsNullOrWhiteSpace(stderr) ? "(empty)" : stderr.Trim());
                Fail("Build failed", details);
                return;
            }

            // Find the freshest VolumeMixer.dll under bin\Debug
            string? builtDll = null;
            var binDir = Path.Combine(projDir, "bin", "Debug");
            if (Directory.Exists(binDir))
            {
                builtDll = Directory.GetFiles(binDir, "VolumeMixer.dll", SearchOption.AllDirectories)
                                    .OrderByDescending(File.GetLastWriteTimeUtc)
                                    .FirstOrDefault();
            }

            if (builtDll == null || !File.Exists(builtDll))
            {
                Fail("DLL not found",
                    $"Build succeeded but no VolumeMixer.dll found under:\n{binDir}");
                return;
            }

            var dest = Path.Combine(customModulesDir, "VolumeMixer.dll");
            try
            {
                File.Copy(builtDll, dest, overwrite: true);
            }
            catch (Exception copyEx)
            {
                Fail("Copy failed",
                    $"Built DLL successfully but could not copy to Dockly.\n\nFrom: {builtDll}\nTo:   {dest}\n\n{copyEx.GetType().Name}: {copyEx.Message}\n\nIs Dockly currently running and holding the DLL open?");
                return;
            }

            Debug.WriteLine($"[Deploy] Copied {builtDll} -> {dest}");
            button.Content = "✓ Deployed";
            ToolTip.SetTip(button, $"Built and copied to:\n{dest}");
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

    // Auto-detect the VolumeMixer.csproj. Prefer the editor-local copy (the one
    // RunModule actually references); fall back to any other VolumeMixer.csproj
    // found by walking up the directory tree.
    private string? FindVolumeMixerProject()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            var editorLocal = Path.Combine(dir, "Docklys.VolumeMixer", "VolumeMixer.csproj");
            if (File.Exists(editorLocal)) return editorLocal;

            var parent = Directory.GetParent(dir);
            if (parent == null) break;
            dir = parent.FullName;
        }
        return null;
    }

    // Auto-detect Dockly's CustomModules folder. Tries common layouts:
    //   <root>\Dockly\CustomModules
    //   <root>\Dockly\Dockly\CustomModules
    // Falls back to a running Dockly process's directory if nothing matches.
    private string? FindDocklyCustomModulesDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            foreach (var rel in new[] {
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
                    var cm = Path.Combine(exeDir, "CustomModules");
                    Directory.CreateDirectory(cm);
                    return cm;
                }
            }
        }
        catch { }

        return null;
    }

    private async void ReloadModule_Click(object sender, RoutedEventArgs e)
    {
        var control = this.FindControl<Control>("ModuleControl");
        if (control == null) return;

        var parent = control.Parent as Panel;
        if (parent == null) return;

        int index = parent.Children.IndexOf(control);
        parent.Children.Remove(control);  // detach → Unloaded fires

        await Task.Delay(150);            // simulate the gap between profile unload and reload

        if (index >= 0 && index <= parent.Children.Count)
            parent.Children.Insert(index, control);  // re-attach → Loaded fires
        else
            parent.Children.Add(control);
    }

    private void OpenOutputModuleDllFolder_Click(object sender, RoutedEventArgs e)
    {
        // Locate DocklysEditor root (reuse same approach as OpenWebPFolder_Click)
        var searchDir = AppContext.BaseDirectory;
        string? editorRoot = null;
        while (searchDir != null && !Path.GetFileName(searchDir).Equals("DocklysEditor", StringComparison.OrdinalIgnoreCase))
        {
            searchDir = Directory.GetParent(searchDir)?.FullName;
        }

        if (searchDir != null)
            editorRoot = searchDir;

        if (editorRoot == null)
        {
            Debug.WriteLine("Could not locate DocklysEditor root to open OutputModuleDLL folder");
            return;
        }

        var outputDir = Path.Combine(editorRoot, "OutputModuleDLL");
        Directory.CreateDirectory(outputDir);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{outputDir}\"",
                UseShellExecute = true,
            };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to open Explorer: {ex}");
        }
    }
}
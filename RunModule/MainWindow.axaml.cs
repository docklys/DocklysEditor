using Avalonia.Controls;
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
using Docklys.ModuleContracts;

namespace RunModule;

public partial class MainWindow : Window
{

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

        // Get button height (approximately 40px + margins)
        var buttonHeight = 60;

        // Calculate window size based on module content
        var windowWidth = Math.Min(Math.Max(desiredSize.Width + 40, 400), maxWidth); // Min 400px, max 90% screen
        var windowHeight = Math.Min(Math.Max(desiredSize.Height + buttonHeight + 40, 300), maxHeight); // Min 300px, max 90% screen

        // Set window size
        this.Width = windowWidth;
        this.Height = windowHeight + 50; // Add extra space for WebP Button and margins
        
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
            var size = new PixelSize(Math.Max(1, (int)Math.Ceiling(desired.Width)), Math.Max(1, (int)Math.Ceiling(desired.Height)));
            var dpi = new Vector(96, 96);

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
            using (var skData = skImage.Encode(SKEncodedImageFormat.Webp, 90))
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
}
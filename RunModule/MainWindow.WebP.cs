using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace RunModule;

public partial class MainWindow
{
    private async Task<string?> PromptWebPSave(string imagesDir)
    {
        var tcs = new TaskCompletionSource<string?>();
        var existing = Directory.Exists(imagesDir) 
            ? Directory.GetFiles(imagesDir, "*.webp")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => n != null)
                .Cast<string>()
                .OrderBy(n => n)
                .ToList()
            : new List<string>();

        var nameBox = new TextBox { Width = 340, Watermark = "Enter name...", Text = "" };
        var listBox = new ListBox { Height = 150, ItemsSource = existing };
        listBox.SelectionChanged += (s, e) => {
            if (listBox.SelectedItem is string selected) nameBox.Text = selected;
        };

        var ok = new Button { Content = "Save", IsDefault = true, Padding = new Thickness(16, 4) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(16, 4), Margin = new Thickness(8, 0, 0, 0) };

        var w = new Window
        {
            Title = "Save WebP Screenshot",
            Width = 400,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = new StackPanel
            {
                Margin = new Thickness(16), Spacing = 8,
                Children = {
                    new TextBlock { Text = "File name", Foreground = Brushes.White },
                    nameBox,
                    new TextBlock { Text = "Or overwrite existing:", Foreground = Brushes.White, Margin = new Thickness(0, 8, 0, 0) },
                    listBox,
                    new StackPanel { 
                        Orientation = Orientation.Horizontal, 
                        HorizontalAlignment = HorizontalAlignment.Right, 
                        Margin = new Thickness(0, 12, 0, 0), 
                        Children = { ok, cancel } 
                    }
                }
            }
        };
        StyleDialog(w);

        ok.Click += (_, _) => {
            var val = nameBox.Text?.Trim();
            if (!string.IsNullOrEmpty(val)) { tcs.TrySetResult(val); w.Close(); }
        };
        cancel.Click += (_, _) => { tcs.TrySetResult(null); w.Close(); };
        w.Closed += (_, _) => tcs.TrySetResult(null);
        nameBox.AttachedToVisualTree += (_, _) => nameBox.Focus();

        await w.ShowDialog(this);
        return await tcs.Task;
    }

    private async void CaptureToWebP_Click(object sender, RoutedEventArgs e)
    {
        if (_currentIndex < 0 || _currentIndex >= _catalog.Count) return;
        var entry = _catalog[_currentIndex];
        var projectDir = Path.GetDirectoryName(entry.CsprojPath);
        if (projectDir == null) return;

        var imagesDir = Path.Combine(projectDir, "Images");
        Directory.CreateDirectory(imagesDir);

        var name = await PromptWebPSave(imagesDir);
        if (string.IsNullOrEmpty(name)) return;

        var savePath = Path.Combine(imagesDir, name + ".webp");

        var slot = this.FindControl<ContentControl>("ActiveModuleSlot");
        var control = slot?.Content as Control;
        if (control == null) return;

        const int captureScale = 4;
        var originalBounds = control.Bounds;
        var originalTransform = control.RenderTransform;
        var originalTransformOrigin = control.RenderTransformOrigin;

        try
        {
            control.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
            control.RenderTransform = new ScaleTransform(1.0, 1.0);

            control.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var desired = control.DesiredSize;
            var size = new PixelSize(Math.Max(1, (int)Math.Ceiling(desired.Width * captureScale)), Math.Max(1, (int)Math.Ceiling(desired.Height * captureScale)));
            var dpi = new Vector(96 * captureScale, 96 * captureScale);

            using var rtb = new RenderTargetBitmap(size, dpi);
            control.Arrange(new Rect(0, 0, desired.Width, desired.Height));
            rtb.Render(control);

            using var stream = new MemoryStream();
            rtb.Save(stream);
            stream.Seek(0, SeekOrigin.Begin);

            using var skStream = new SKManagedStream(stream);
            var decoded = SKBitmap.Decode(skStream);
            if (decoded == null) return;
            
            var skBitmap = new SKBitmap(decoded.Width, decoded.Height);
            using (var canvas = new SKCanvas(skBitmap))
            {
                canvas.DrawBitmap(decoded, 0, 0);
                decoded.Dispose();

                var webViews = control.GetVisualDescendants()
                    .OfType<Control>()
                    .Where(c => c.GetType().Name.Contains("WebView"))
                    .ToList();

                if (webViews.Any())
                {
                    const double WebViewPadding = 6.0;
                    const double ScrollbarExtra = 15.0;

                    foreach (var wv in webViews)
                    {
                        var wvBitmap = await CaptureWebViewAsync(wv);
                        if (wvBitmap == null) continue;

                        var transform = wv.TransformToVisual(control);
                        if (transform.HasValue)
                        {
                            var rect = new Rect(wv.Bounds.Size).TransformToAABB(transform.Value);
                            var visibleRect = new SKRect(
                                (float)((rect.X + WebViewPadding) * captureScale),
                                (float)((rect.Y + WebViewPadding) * captureScale),
                                (float)((rect.Right - WebViewPadding) * captureScale),
                                (float)((rect.Bottom - WebViewPadding) * captureScale)
                            );

                            double logicalVisibleW = rect.Width - (WebViewPadding * 2);
                            double logicalVisibleH = rect.Height - (WebViewPadding * 2);
                            float srcVisibleW = wvBitmap.Width * (float)(logicalVisibleW / (logicalVisibleW + ScrollbarExtra));
                            float srcVisibleH = wvBitmap.Height * (float)(logicalVisibleH / (logicalVisibleH + ScrollbarExtra));
                            var srcRect = new SKRect(0, 0, srcVisibleW, srcVisibleH);
                            
                            using var path = new SKPath();
                            float cornerRadius = 4f * captureScale;
                            path.AddRoundRect(visibleRect, cornerRadius, cornerRadius);
                            
                            canvas.Save();
                            canvas.ClipPath(path, SKClipOperation.Intersect, antialias: true);
                            canvas.DrawBitmap(wvBitmap, srcRect, visibleRect);
                            canvas.Restore();
                        }
                        wvBitmap.Dispose();
                    }
                }
            }

            using (var output = File.OpenWrite(savePath))
            using (var skImage = SKImage.FromBitmap(skBitmap))
            using (var skData = skImage.Encode(SKEncodedImageFormat.Webp, 100))
            {
                skData.SaveTo(output);
            }

            if (File.Exists(savePath))
                await ShowMessageDialog("Screenshot Saved", $"✓ WebP file created:\n{savePath}");
        }
        catch (Exception ex)
        {
            await ShowMessageDialog("Capture Error", ex.Message);
        }
        finally
        {
            control.RenderTransform = originalTransform;
            control.RenderTransformOrigin = originalTransformOrigin;
            ApplyZoomToActiveModule(GetCurrentZoom());
        }
    }

    private async void CaptureDock_Click(object sender, RoutedEventArgs e)
    {
        var dockly = Process.GetProcessesByName("Dockly.Desktop").FirstOrDefault()
                  ?? Process.GetProcessesByName("Dockly").FirstOrDefault();

        if (dockly == null)
        {
            await ShowMessageDialog("Capture Dock", "Dockly.Desktop is not running. Please start it first.");
            return;
        }

        IntPtr hwnd = FindMainWindowForProcess(dockly.Id);
        if (hwnd == IntPtr.Zero)
        {
            await ShowMessageDialog("Capture Dock", "Could not find Dockly main window handle.");
            return;
        }

        // Bring to front
        SetForegroundWindow(hwnd);
        // Ensure "slide in" - for Dockly, this usually means ensuring it's not hidden.
        // We'll also try to send a 'Home' key which often triggers the dock in such apps, 
        // or just wait for it to be visible.
        await Task.Delay(800);

        if (_currentIndex < 0 || _currentIndex >= _catalog.Count) return;
        var entry = _catalog[_currentIndex];
        var projectDir = Path.GetDirectoryName(entry.CsprojPath);
        if (projectDir == null) return;

        var imagesDir = Path.Combine(projectDir, "Images");
        Directory.CreateDirectory(imagesDir);

        var name = await PromptWebPSave(imagesDir);
        if (string.IsNullOrEmpty(name)) return;
        var savePath = Path.Combine(imagesDir, name + ".webp");

        try
        {
            bool ok = await RequestDockCaptureAsync(savePath);
            if (ok && File.Exists(savePath))
                await ShowMessageDialog("Capture Dock", $"✓ Screenshot saved to {savePath}");
            else
                await ShowMessageDialog("Capture Failed",
                    "Dockly did not return a screenshot. Make sure you're running an up-to-date Dockly.Desktop build.");
        }
        catch (Exception ex)
        {
            await ShowMessageDialog("Capture Error", ex.Message);
        }
    }

    // Drives the file-based capture IPC. Dockly's window is excluded from OS screen capture
    // (WDA_EXCLUDEFROMCAPTURE), so it can't be grabbed externally — instead we drop a request
    // file naming the output path and let the running dock render itself into that WebP.
    private static async Task<bool> RequestDockCaptureAsync(string outputPath)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Docklys", "capture-requests");
        Directory.CreateDirectory(dir);

        var id = Guid.NewGuid().ToString("N");
        var reqPath = Path.Combine(dir, id + ".req");
        var donePath = Path.Combine(dir, id + ".done");

        // Write the temp file OUTSIDE the watched folder, then move it in: a move into the
        // directory raises a single Created event with fully-flushed content (a same-folder
        // rename would raise Renamed instead, which the watcher isn't listening for).
        var tmp = Path.Combine(Path.GetTempPath(), id + ".req.tmp");
        File.WriteAllText(tmp, outputPath);
        File.Move(tmp, reqPath);

        // Wait up to ~15s for the dock to render and signal completion.
        for (int i = 0; i < 300; i++)
        {
            if (File.Exists(donePath))
            {
                string status = "";
                try { status = File.ReadAllText(donePath).Trim(); } catch { }
                try { File.Delete(donePath); } catch { }
                return status.StartsWith("OK");
            }
            await Task.Delay(50);
        }
        try { if (File.Exists(reqPath)) File.Delete(reqPath); } catch { }
        return false;
    }

    private SKBitmap? CaptureWindow(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var rect)) return null;
        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0) return null;

        // Dockly's main window is transparent and GPU/DirectComposition-composited, so a plain
        // screen BitBlt only captures the desktop *behind* it. PW_RENDERFULLCONTENT (0x2) asks the
        // window to render its own content (including the composed surface) into our DC instead.
        IntPtr hdcScreen = GetDC(IntPtr.Zero);
        IntPtr hdcDest = CreateCompatibleDC(hdcScreen);
        IntPtr hBitmap = CreateCompatibleBitmap(hdcScreen, width, height);
        IntPtr hOld = SelectObject(hdcDest, hBitmap);

        PrintWindow(hwnd, hdcDest, 0x00000002);

        // Opaque: GDI leaves the alpha byte at 0, which would otherwise encode as a fully
        // transparent ("empty") WebP even though the RGB pixels are correct.
        var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);

        BITMAPINFO bmi = new BITMAPINFO();
        bmi.bmiHeader.biSize = (uint)Marshal.SizeOf(typeof(BITMAPINFOHEADER));
        bmi.bmiHeader.biWidth = width;
        bmi.bmiHeader.biHeight = -height;
        bmi.bmiHeader.biPlanes = 1;
        bmi.bmiHeader.biBitCount = 32;
        bmi.bmiHeader.biCompression = 0;

        GetDIBits(hdcDest, hBitmap, 0, (uint)height, bitmap.GetPixels(), ref bmi, 0);

        SelectObject(hdcDest, hOld);
        DeleteObject(hBitmap);
        DeleteDC(hdcDest);
        ReleaseDC(IntPtr.Zero, hdcScreen);

        return bitmap;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    private static IntPtr FindMainWindowForProcess(int pid)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out uint wPid);
            if (wPid == (uint)pid && IsWindowVisible(hwnd))
            {
                found = hwnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hDC, int nWidth, int nHeight);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint uStartScan, uint cScanLines, IntPtr lpvBits, ref BITMAPINFO lpbi, uint uUsage);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }

    private double GetCurrentZoom()
    {
        var slider = this.FindControl<Slider>("ZoomSlider");
        return slider?.Value ?? 100.0;
    }
}

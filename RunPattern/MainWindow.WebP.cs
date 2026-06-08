using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace RunPattern;

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

    private async void CaptureToWebP_Click(object? sender, RoutedEventArgs e)
    {
        if (_currentIndex < 0 || _currentIndex >= _catalog.Count) return;
        var entry = _catalog[_currentIndex];
        var projectDir = Path.GetDirectoryName(entry.CsprojPath);
        if (projectDir == null) return;

        var imagesDir = Path.Combine(projectDir, "Images");
        Directory.CreateDirectory(imagesDir);

        var name = await PromptWebPSave(imagesDir);
        if (string.IsNullOrEmpty(name)) return;

        var previewBorder = this.FindControl<Border>("PreviewBorder");
        if (previewBorder == null) return;

        _interaction?.OnPointerMoved(0.5, 0.5);
        await Task.Delay(80);

        const int captureScale = 4;

        try
        {
            previewBorder.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var desired = previewBorder.DesiredSize;
            if (desired.Width <= 0 || desired.Height <= 0)
            {
                desired = new Size(
                    previewBorder.Bounds.Width > 0 ? previewBorder.Bounds.Width : this.Width - 40,
                    previewBorder.Bounds.Height > 0 ? previewBorder.Bounds.Height : this.Height - 80);
            }

            var size = new PixelSize(
                Math.Max(1, (int)Math.Ceiling(desired.Width * captureScale)),
                Math.Max(1, (int)Math.Ceiling(desired.Height * captureScale)));
            var dpi = new Vector(96 * captureScale, 96 * captureScale);

            using var rtb = new RenderTargetBitmap(size, dpi);
            previewBorder.Arrange(new Rect(0, 0, desired.Width, desired.Height));
            rtb.Render(previewBorder);

            using var stream = new MemoryStream();
            rtb.Save(stream);
            stream.Seek(0, SeekOrigin.Begin);

            using var skStream = new SKManagedStream(stream);
            using var decoded = SKBitmap.Decode(skStream);
            if (decoded == null) return;

            var savePath = Path.Combine(imagesDir, name + ".webp");
            using (var output = File.OpenWrite(savePath))
            using (var skImage = SKImage.FromBitmap(decoded))
            using (var skData = skImage.Encode(SKEncodedImageFormat.Webp, 100))
            {
                skData.SaveTo(output);
            }

            if (File.Exists(savePath))
                await ShowMessageDialog("Screenshot Saved", $"✓ WebP file created:\n{savePath}");
        }
        catch (Exception ex)
        {
            await ShowMessageDialog("Screenshot failed", ex.Message);
        }
        finally
        {
            _interaction?.OnPointerMoved(0.5, 0.5);
        }
    }

    private async void CaptureDock_Click(object? sender, RoutedEventArgs e)
    {
        if (_currentIndex < 0 || _currentIndex >= _catalog.Count) return;
        var entry = _catalog[_currentIndex];
        var projectDir = Path.GetDirectoryName(entry.CsprojPath);
        if (projectDir == null) return;

        var imagesDir = Path.Combine(projectDir, "Images");
        Directory.CreateDirectory(imagesDir);

        var name = await PromptWebPSave(imagesDir);
        if (string.IsNullOrEmpty(name)) return;

        var dockly = Process.GetProcessesByName("Dockly.Desktop").FirstOrDefault()
                  ?? Process.GetProcessesByName("Dockly").FirstOrDefault();

        if (dockly == null)
        {
            await ShowMessageDialog("Capture Dock", "Dockly.Desktop is not running. Please start it first.");
            return;
        }

        IntPtr hwnd = dockly.MainWindowHandle;
        if (hwnd == IntPtr.Zero)
        {
            await ShowMessageDialog("Capture Dock", "Could not find Dockly main window handle.");
            return;
        }

        SetForegroundWindow(hwnd);
        await Task.Delay(800);

        var savePath = Path.Combine(imagesDir, name + ".webp");

        try
        {
            using var bitmap = CaptureWindow(hwnd);
            if (bitmap != null)
            {
                using var skImage = SKImage.FromBitmap(bitmap);
                using var skData = skImage.Encode(SKEncodedImageFormat.Webp, 100);
                using var output = File.Create(savePath);
                skData.SaveTo(output);
                await ShowMessageDialog("Capture Dock", $"✓ Screenshot saved to {savePath}");
            }
            else
            {
                await ShowMessageDialog("Capture Failed", "Could not capture Dockly window pixels.");
            }
        }
        catch (Exception ex)
        {
            await ShowMessageDialog("Capture Error", ex.Message);
        }
    }

    private void OnOpenWebPFolderClick(object? sender, RoutedEventArgs e)
    {
        if (_currentIndex >= 0 && _currentIndex < _catalog.Count)
        {
            var entry = _catalog[_currentIndex];
            var projectDir = Path.GetDirectoryName(entry.CsprojPath);
            if (projectDir != null)
            {
                var dir = Path.Combine(projectDir, "Images");
                Directory.CreateDirectory(dir);
                OpenFolder(dir);
                return;
            }
        }
        _ = ShowMessageDialog("Open folder", "No pattern selected.");
    }

    private SKBitmap? CaptureWindow(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var rect)) return null;
        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0) return null;

        IntPtr hdcSrc = GetWindowDC(hwnd);
        IntPtr hdcDest = CreateCompatibleDC(hdcSrc);
        IntPtr hBitmap = CreateCompatibleBitmap(hdcSrc, width, height);
        IntPtr hOld = SelectObject(hdcDest, hBitmap);

        BitBlt(hdcDest, 0, 0, width, height, hdcSrc, 0, 0, 0x00CC0020 | 0x40000000);

        var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);

        BITMAPINFO bmi = new BITMAPINFO();
        bmi.bmiHeader.biSize = (uint)Marshal.SizeOf(typeof(BITMAPINFOHEADER));
        bmi.bmiHeader.biWidth = width;
        bmi.bmiHeader.biHeight = -height;
        bmi.bmiHeader.biPlanes = 1;
        bmi.bmiHeader.biBitCount = 32;
        bmi.bmiHeader.biCompression = 0;

        GetDIBits(hdcSrc, hBitmap, 0, (uint)height, bitmap.GetPixels(), ref bmi, 0);

        SelectObject(hdcDest, hOld);
        DeleteObject(hBitmap);
        DeleteDC(hdcDest);
        ReleaseDC(hwnd, hdcSrc);

        return bitmap;
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

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight, IntPtr hdcSrc, int nXSrc, int nYSrc, int dwRop);

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
}

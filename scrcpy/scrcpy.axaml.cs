using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Docklys.ModuleContracts;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace scrcpy
{
    public partial class Scrcpy : UserControl, IModule, IResizable
    {
        // Identification
        public string Id => "scrcpy";
        public string ModuleName => "scrcpy";
        public string ModuleVersion => "1.1.0";
        public string Category => "Default";
        public string[] Tags => new[] { "scrcpy", "android", "mirror", "phone" };

        public int PreferredTileWidth => 3;
        public int PreferredTileHeight => 5;

        // Compatibility
        public string MinAppVersion => "1.0.0";
        public string MaxAppVersion => "2.0.0";
        public string[] SupportedPlatforms => new[] { "Windows", "Linux", "Mac" };

        // Unique Module ID (set by the main app)
        private string _uniqueModuleId = string.Empty;
        public string UniqueModuleId => _uniqueModuleId;
        public void SetModuleId(string uniqueModuleId)
        {
            _uniqueModuleId = uniqueModuleId;
            EnsureModuleDataRoot();
        }
        public void PrintModuleId() => Console.WriteLine($"Module ID: {UniqueModuleId}");

        // IResizable — required by the interface, but this fixed-size tile never raises it.
#pragma warning disable CS0067
        public event Action<int, int>? TileResizeRequested;
#pragma warning restore CS0067
        public void SetTileSize(int width, int height) { }

        private IDeviceMirrorSession? _session;
        private WriteableBitmap? _bitmap;
        private int _bmpWidth, _bmpHeight;
        private readonly object _frameSync = new();
        private byte[]? _pendingFrame;
        private int _pendingFrameWidth, _pendingFrameHeight;
        private bool _frameUpdateQueued;
        private bool _pointerDown;
        // Retain a valid device point so an off-screen release can still lift the touch.
        private int _lastTouchX, _lastTouchY;
        private CancellationTokenSource? _sessionCts;

        public Scrcpy()
        {
            InitializeComponent();

            AttachedToVisualTree += OnAttached;
            DetachedFromVisualTree += OnDetached;

            InputSurface.PointerPressed += OnPointerPressed;
            InputSurface.PointerMoved += OnPointerMoved;
            InputSurface.PointerReleased += OnPointerReleased;
            InputSurface.PointerExited += OnPointerExited;
            InputSurface.PointerCaptureLost += OnPointerCaptureLost;
            InputSurface.PointerWheelChanged += OnPointerWheel;
            InputSurface.KeyDown += OnKeyDown;
            InputSurface.TextInput += OnTextInput;

        }

        private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
        {
            _sessionCts?.Dispose();
            _sessionCts = new CancellationTokenSource();
            _ = StartAsync(_sessionCts.Token);
        }

        private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
        {
            try { _sessionCts?.Cancel(); } catch { }
            var session = _session;
            _session = null;
            if (session != null) _ = session.DisposeAsync().AsTask();
        }

        private async Task StartAsync(CancellationToken cancellationToken)
        {
            if (_session != null) return;

            if (!DocklysHostServices.TryGetDeviceMirror(out var deviceMirror))
            {
                StatusText.Text = "Device mirroring is unavailable in this Docklys host.";
                return;
            }

            try
            {
                var session = await deviceMirror.StartAsync(new DeviceMirrorStartOptions
                {
                    MaxSize = 1024,
                    MaxFps = 60,
                    VideoBitRate = 8_000_000,
                }, cancellationToken);
                if (cancellationToken.IsCancellationRequested)
                {
                    await session.DisposeAsync();
                    return;
                }

                session.StatusChanged += status =>
                    Dispatcher.UIThread.Post(() => StatusText.Text = status);
                session.FrameReady += OnFrameReady;
                _session = session;
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => StatusText.Text = ex.Message);
            }
        }

        private void OnFrameReady(DeviceMirrorFrame mirrorFrame)
        {
            if (_sessionCts?.IsCancellationRequested != false) return;
            var frame = mirrorFrame.BgraPixels;
            var width = mirrorFrame.Width;
            var height = mirrorFrame.Height;

            // Frame callbacks run on the decoder thread. Coalesce them to one normal UI
            // job: Render-priority work can wait for the next render pass, which may not
            // happen after the cursor leaves the tile. Keeping only the latest frame also
            // prevents a stale queue from making the mirror look frozen after UI activity.
            lock (_frameSync)
            {
                _pendingFrame = frame;
                _pendingFrameWidth = width;
                _pendingFrameHeight = height;
                if (_frameUpdateQueued) return;
                _frameUpdateQueued = true;
            }

            Dispatcher.UIThread.Post(RenderLatestFrame, DispatcherPriority.Normal);
        }

        private void RenderLatestFrame()
        {
            byte[]? frame;
            int width, height;
            lock (_frameSync)
            {
                frame = _pendingFrame;
                width = _pendingFrameWidth;
                height = _pendingFrameHeight;
                _pendingFrame = null;
                _frameUpdateQueued = false;
            }

            if (_sessionCts?.IsCancellationRequested != false || frame == null) return;

            if (_bitmap == null || _bmpWidth != width || _bmpHeight != height)
            {
                _bitmap?.Dispose();
                _bitmap = new WriteableBitmap(
                    new PixelSize(width, height),
                    new Vector(96, 96),
                    PixelFormat.Bgra8888,
                    AlphaFormat.Opaque);
                _bmpWidth = width;
                _bmpHeight = height;
                Screen.Source = _bitmap;
            }

            using (var fb = _bitmap.Lock())
            {
                // Row by row: the locked buffer's stride need not equal width*4.
                var srcStride = width * 4;
                if (fb.RowBytes == srcStride)
                {
                    Marshal.Copy(frame, 0, fb.Address, Math.Min(frame.Length, fb.RowBytes * height));
                }
                else
                {
                    for (var y = 0; y < height; y++)
                        Marshal.Copy(frame, y * srcStride, fb.Address + y * fb.RowBytes, srcStride);
                }
            }

            StatusText.IsVisible = false;
            Screen.InvalidateVisual();
        }

        /// <summary>
        /// Maps a pointer position on the input surface to device coordinates, accounting for the
        /// letterboxing introduced by Stretch="Uniform". Returns false when the pointer is in the
        /// letterbox rather than on the phone screen.
        /// </summary>
        private bool TryMapToDevice(Point p, out int x, out int y)
        {
            x = y = 0;
            var session = _session;
            if (session == null || session.VideoWidth == 0 || session.VideoHeight == 0) return false;

            var bounds = InputSurface.Bounds;
            if (bounds.Width <= 0 || bounds.Height <= 0) return false;

            var scale = Math.Min(bounds.Width / session.VideoWidth, bounds.Height / session.VideoHeight);
            var dispW = session.VideoWidth * scale;
            var dispH = session.VideoHeight * scale;
            var offX = (bounds.Width - dispW) / 2;
            var offY = (bounds.Height - dispH) / 2;

            var localX = p.X - offX;
            var localY = p.Y - offY;
            if (localX < 0 || localY < 0 || localX >= dispW || localY >= dispH) return false;

            x = Math.Clamp((int)(localX / scale), 0, session.VideoWidth - 1);
            y = Math.Clamp((int)(localY / scale), 0, session.VideoHeight - 1);
            return true;
        }

        private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            var session = _session;
            if (session == null) return;
            if (!TryMapToDevice(e.GetPosition(InputSurface), out var x, out var y)) return;

            _pointerDown = true;
            _lastTouchX = x;
            _lastTouchY = y;
            InputSurface.Focus();
            e.Pointer.Capture(InputSurface);
            _ = session.SendTouchAsync(DeviceMirrorTouchAction.Down, x, y, 1.0f, _sessionCts?.Token ?? CancellationToken.None);
        }

        private void OnPointerMoved(object? sender, PointerEventArgs e)
        {
            if (!_pointerDown) return;
            var session = _session;
            if (session == null) return;
            if (!TryMapToDevice(e.GetPosition(InputSurface), out var x, out var y)) return;

            _lastTouchX = x;
            _lastTouchY = y;
            _ = session.SendTouchAsync(DeviceMirrorTouchAction.Move, x, y, 1.0f, _sessionCts?.Token ?? CancellationToken.None);
        }

        private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            EndActiveTouch();
            e.Pointer.Capture(null);
        }

        // A captured pointer can be released outside InputSurface. If that is not
        // translated to an Android UP event, scrcpy leaves the mouse button latched
        // and the displayed phone only starts responding again after another click.
        private void OnPointerExited(object? sender, PointerEventArgs e)
        {
            if (!_pointerDown) return;
            EndActiveTouch();
            e.Pointer.Capture(null);
        }

        // Also clear a touch cancelled by the host or a focus change while captured.
        private void OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) => EndActiveTouch();

        private void EndActiveTouch()
        {
            if (!_pointerDown) return;
            _pointerDown = false;

            var session = _session;
            if (session == null) return;

            // Pressure 0 on release, matching what the real client sends for a lift.
            _ = session.SendTouchAsync(DeviceMirrorTouchAction.Up, _lastTouchX, _lastTouchY, 0f, _sessionCts?.Token ?? CancellationToken.None);
        }

        private void OnPointerWheel(object? sender, PointerWheelEventArgs e)
        {
            var session = _session;
            if (session == null) return;
            if (!TryMapToDevice(e.GetPosition(InputSurface), out var x, out var y)) return;

            _ = session.SendScrollAsync(x, y, (float)e.Delta.X, (float)e.Delta.Y, _sessionCts?.Token ?? CancellationToken.None);
            e.Handled = true;
        }

        private void OnKeyDown(object? sender, KeyEventArgs e)
        {
            var key = e.Key switch
            {
                Key.Back => DeviceMirrorKey.Backspace,
                Key.Enter => DeviceMirrorKey.Enter,
                Key.Escape => DeviceMirrorKey.Back,
                _ => (DeviceMirrorKey?)null,
            };
            if (key is null) return;
            SendKey(key.Value);
            e.Handled = true;
        }

        private void OnTextInput(object? sender, TextInputEventArgs e)
        {
            var session = _session;
            if (session == null || string.IsNullOrEmpty(e.Text)) return;
            _ = session.SendTextAsync(e.Text, _sessionCts?.Token ?? CancellationToken.None);
            e.Handled = true;
        }

        private void SendKey(DeviceMirrorKey key)
        {
            var session = _session;
            if (session == null) return;
            _ = session.SendKeyAsync(key, _sessionCts?.Token ?? CancellationToken.None);
        }

        private void EnsureModuleDataRoot()
        {
            if (string.IsNullOrWhiteSpace(UniqueModuleId)) return;
            var safeId = string.Concat(UniqueModuleId.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Docklys", "Modules", "scrcpy", safeId);
            Directory.CreateDirectory(root);
        }
    }
}

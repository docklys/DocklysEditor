using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Docklys.ModuleContracts;
using System;
using System.IO;
using System.Reflection;

namespace spotify
{
    public partial class spotify : UserControl, IModule, IResizable
    {
        // IModule
        public string Id => "spotify";
        public string ModuleName => "spotify";
        public string ModuleVersion => "1.0.0";
        public string Category => "Media";
        public string[] Tags => new[] { "spotify", "music", "streaming" };

        public int TileWidth => 2;
        public int TileHeight => 3;

        public string MinAppVersion => "1.0.0";
        public string MaxAppVersion => "2.0.0";
        public string[] SupportedPlatforms => new[] { "Windows", "Linux", "Mac" };

        private string _uniqueModuleId = string.Empty;
        public string UniqueModuleId => _uniqueModuleId;
        public void SetModuleId(string id) => _uniqueModuleId = id;
        public void PrintModuleId() => Console.WriteLine($"Module ID: {UniqueModuleId}");

        // IResizable
        public event Action<int, int>? TileResizeRequested;

        public void SetTileSize(int width, int height)
        {
            _currentW = width;
            _currentH = height;
            RefreshSizeDisplay();
        }

        private int _currentW = 2;
        private int _currentH = 3;
        private bool _shiftDown;
        private bool _inTriggerZone;
        private TopLevel? _topLevel;
        private bool _webViewCreated;

        private const string SpotifyUrl = "https://open.spotify.com/";

        public spotify()
        {
            InitializeComponent();

            WidthMinus.Click  += (_, _) => Adjust(-1,  0);
            WidthPlus.Click   += (_, _) => Adjust(+1,  0);
            HeightMinus.Click += (_, _) => Adjust( 0, -1);
            HeightPlus.Click  += (_, _) => Adjust( 0, +1);

            ResizeTriggerZone.PointerEntered += (_, _) => { _inTriggerZone = true;  SyncOverlay(); };
            ResizeTriggerZone.PointerExited  += (_, _) => { _inTriggerZone = false; SyncOverlay(); };

            RefreshSizeDisplay();
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);

            _topLevel = TopLevel.GetTopLevel(this);
            if (_topLevel != null)
            {
                _topLevel.KeyDown += OnTopLevelKeyDown;
                _topLevel.KeyUp   += OnTopLevelKeyUp;
            }

            if (!_webViewCreated)
            {
                _webViewCreated = true;
                TryCreateWebView();
            }
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            if (_topLevel != null)
            {
                _topLevel.KeyDown -= OnTopLevelKeyDown;
                _topLevel.KeyUp   -= OnTopLevelKeyUp;
                _topLevel = null;
            }
            _shiftDown = false;
        }

        private void TryCreateWebView()
        {
            try
            {
                var webViewType = ResolveWebViewType();
                if (webViewType == null)
                {
                    ShowError("WebView unavailable: Avalonia.WebView.dll not found in host directory.\n(This module requires the main Dockly app — WebView is not available in RunModule.)");
                    return;
                }

                var webView = (Control)Activator.CreateInstance(webViewType)!;
                webView.HorizontalAlignment = HorizontalAlignment.Stretch;
                webView.VerticalAlignment   = VerticalAlignment.Stretch;

                var urlProp = webViewType.GetProperty("Url",
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);

                // Set URL after the control is in the visual tree to avoid
                // ArgumentOutOfRangeException from the WebView2 runtime not being ready yet.
                webView.Loaded += (_, _) =>
                {
                    try { urlProp?.SetValue(webView, new Uri(SpotifyUrl)); }
                    catch (Exception ex) { ShowError($"Failed to navigate to Spotify:\n{ex.InnerException?.Message ?? ex.Message}"); }
                };

                WebViewContainer.Children.Add(webView);
            }
            catch (Exception ex)
            {
                var sb = new System.Text.StringBuilder();
                var e = (Exception?)ex;
                while (e != null)
                {
                    sb.AppendLine($"{e.GetType().FullName}: {e.Message}");
                    if (e.StackTrace != null)
                    {
                        foreach (var line in e.StackTrace.Split('\n'))
                            sb.AppendLine("  " + line.TrimEnd());
                    }
                    e = e.InnerException;
                    if (e != null) sb.AppendLine("  ── inner ──");
                }
                ShowError(sb.ToString().TrimEnd());
            }
        }

        // Loads Avalonia.WebView.dll from the host's base directory and returns
        // the AvaloniaWebView.WebView type. Returns null if the DLL is not present
        // (e.g. in RunModule) so the caller can show a friendly error instead of crashing.
        private static Type? ResolveWebViewType()
        {
            // Check if it's already loaded (e.g. SettingsWindow opened first)
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.GetName().Name == "Avalonia.WebView")
                {
                    var t = asm.GetType("AvaloniaWebView.WebView");
                    if (t != null) return t;
                }
            }

            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var dllPath = Path.Combine(baseDir, "Avalonia.WebView.dll");
            if (!File.Exists(dllPath)) return null;

            // Load dependencies before the main DLL so GetType succeeds
            foreach (var dep in new[] { "WebView.Core.dll", "AvaloniaWebView.Shared.dll" })
            {
                var p = Path.Combine(baseDir, dep);
                if (File.Exists(p)) try { Assembly.LoadFrom(p); } catch { }
            }

            try
            {
                var asm = Assembly.LoadFrom(dllPath);
                return asm.GetType("AvaloniaWebView.WebView");
            }
            catch { return null; }
        }

        private void ShowError(string message)
        {
            Console.WriteLine($"[Spotify] {message}");
            var copyBtn = new Button
            {
                Content             = "Copy error",
                Margin              = new Thickness(0, 0, 0, 6),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            copyBtn.Click += async (_, _) =>
            {
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard != null) await clipboard.SetTextAsync(message);
            };
            var panel = new StackPanel
            {
                Margin              = new Thickness(8),
                VerticalAlignment   = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            panel.Children.Add(copyBtn);
            panel.Children.Add(new TextBlock
            {
                Text         = message,
                Foreground   = new SolidColorBrush(Colors.OrangeRed),
                FontSize     = 10,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            });
            WebViewContainer.Children.Add(panel);
        }

        private void OnTopLevelKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key is Key.LeftShift or Key.RightShift) { _shiftDown = true;  SyncOverlay(); }
        }

        private void OnTopLevelKeyUp(object? sender, KeyEventArgs e)
        {
            if (e.Key is Key.LeftShift or Key.RightShift) { _shiftDown = false; SyncOverlay(); }
        }

        private void SyncOverlay()
        {
            ResizeOverlay.IsVisible = _shiftDown && _inTriggerZone;
        }

        private void Adjust(int dw, int dh)
        {
            int newW = Math.Max(1, Math.Min(8, _currentW + dw));
            int newH = Math.Max(1, Math.Min(8, _currentH + dh));
            if (newW == _currentW && newH == _currentH) return;

            _currentW = newW;
            _currentH = newH;
            RefreshSizeDisplay();
            TileResizeRequested?.Invoke(_currentW, _currentH);
        }

        private void RefreshSizeDisplay()
        {
            if (WidthDisplay  != null) WidthDisplay.Text  = _currentW.ToString();
            if (HeightDisplay != null) HeightDisplay.Text = _currentH.ToString();
        }
    }
}

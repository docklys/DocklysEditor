using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Docklys.ModuleContracts;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace spotify
{
    public partial class spotify : UserControl, IModule, IResizable, IInteractionFreezable
    {
        // IModule
        public string Id => "spotify";
        public string ModuleName => "spotify";
        public string ModuleVersion => "1.0.0";
        public string Category => "Media";
        public string[] Tags => new[] { "spotify", "music", "streaming" };

        public int PreferredTileWidth => 2;
        public int PreferredTileHeight => 3;

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

            Console.WriteLine($"[Spotify] SetTileSize called -> width={width},height={height}, webViewCreated={_webViewCreated}, webViewIsNull={_webView==null}, cachedHwnd={_webViewHwnd}");

            // Host requested a tile-size change — aggressively force the WebView to
            // relayout and resync immediately so the new size is visible without
            // reloading the module.
            try
            {
                // Clear caches so lookups and zoom reapplication run fresh.
                _lastAppliedVisualZoom = double.NaN;
                _webViewHwnd = IntPtr.Zero;

                // Run on UI thread: if we already have a WebView, force relayout and
                // run several follow-up syncs. If the WebView isn't created yet,
                // try to create it and start a short retry timer that will apply
                // the relayout as soon as the control appears.
                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        Console.WriteLine("[Spotify] SetTileSize: performing relayout/resync attempt");
                        if (_webView == null)
                        {
                            if (!_webViewCreated) TryCreateWebView();

                            // Start a short retry timer that will attempt to apply the
                            // relayout as soon as the WebView becomes available.
                            var retry = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                            int attempts = 0;
                            retry.Tick += (_, _) =>
                            {
                                attempts++;
                                if (_webView != null)
                                {
                                    retry.Stop();
                                    try
                                    {
                                        Console.WriteLine("[Spotify] Retry: webview appeared, forcing relayout");
                                        ForceWebViewRelayout();
                                        SyncWebViewHwndToVisualBounds();
                                        ApplyWebViewRoundedCorners();
                                        StartContinuousSync();
                                    }
                                    catch (Exception ex)
                                    {
                                        Console.WriteLine($"[Spotify] Retry relayout failed: {ex.Message}");
                                    }
                                }
                                else if (attempts > 12) // ~1.2s timeout
                                {
                                    retry.Stop();
                                }
                            };
                            retry.Start();
                        }
                        else
                        {
                            if (_webView.IsVisible)
                            {
                                Console.WriteLine("[Spotify] SetTileSize: webView exists, forcing immediate relayout and sync");
                                // Immediate aggressive relayout + multiple follow-ups so
                                // platform races are less likely to leave the HWND stale.
                                ForceWebViewRelayout();
                                SyncWebViewHwndToVisualBounds();
                                ApplyWebViewRoundedCorners();
                                StartContinuousSync();

                                // Two follow-up nudges: one in Background and one when idle.
                                Dispatcher.UIThread.Post(() => { if (_webView.IsVisible) { try { SyncWebViewHwndToVisualBounds(); ApplyWebViewRoundedCorners(); } catch { } } }, DispatcherPriority.Background);
                                Dispatcher.UIThread.Post(() => { if (_webView.IsVisible) { try { SyncWebViewHwndToVisualBounds(); ApplyWebViewRoundedCorners(); } catch { } } }, DispatcherPriority.ContextIdle);

                                // Start a short polling timer that ensures we retry until the
                                // WebView's measured bounds reflect the new tile size. This
                                // catches cases where layout and platform HWND creation are
                                // delayed and our one-shot nudges lost the race.
                                var poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                                int pollAttempts = 0;
                                double lastW = _webView.Bounds.Width;
                                double lastH = _webView.Bounds.Height;
                                poll.Tick += (_, _) =>
                                {
                                    pollAttempts++;
                                    if (!_webView.IsVisible)
                                    {
                                        poll.Stop();
                                        return;
                                    }
                                    try
                                    {
                                        // Force a relayout and sync each tick.
                                        ForceWebViewRelayout();
                                        SyncWebViewHwndToVisualBounds();
                                        ApplyWebViewRoundedCorners();

                                        // If the WebView reports changed bounds or we discovered an HWND,
                                        // stop polling early.
                                        var curW = _webView?.Bounds.Width ?? 0.0;
                                        var curH = _webView?.Bounds.Height ?? 0.0;
                                        var hw = TryGetWebViewHwnd();
                                        if ((Math.Abs(curW - lastW) > 0.5 || Math.Abs(curH - lastH) > 0.5) || hw != IntPtr.Zero)
                                        {
                                            Console.WriteLine($"[Spotify] Poll success: bounds={curW}x{curH}, hwnd={hw}");
                                            poll.Stop();
                                            return;
                                        }
                                        if (pollAttempts > 20) // ~2s
                                        {
                                            Console.WriteLine("[Spotify] Poll timeout: giving up after attempts");
                                            poll.Stop();
                                        }
                                    }
                                    catch { if (pollAttempts > 20) poll.Stop(); }
                                };
                                poll.Start();
                            }
                            else
                            {
                                Console.WriteLine("[Spotify] SetTileSize: webView is hidden, skipping relayout sync to prevent engine crash.");
                            }

                            // Immediate brute-force fallback: enumerate top-level child HWNDs
                            // and set their bounds to our target rect. This will forcibly
                            // resize any native child (including WebView instances) even if
                            // the controller hasn't been exposed via reflection yet.
                            try
                            {
                                var top = TopLevel.GetTopLevel(this);
                                var parentHwnd = top?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
                                if (parentHwnd != IntPtr.Zero)
                                {
                                    var transform = (_webView ?? (Control)this).TransformToVisual(top);
                                    var local = (_webView ?? (Control)this).Bounds;
                                    var composed = transform.HasValue ? transform.Value : Matrix.Identity;
                                    if (top.RenderTransform != null) composed = composed * top.RenderTransform.Value;
                                    var vr = new Rect(0,0, local.Width, local.Height).TransformToAABB(composed);
                                    var scaling = top.RenderScaling;
                                    double pad = WebViewPadding * scaling;
                                    int tx = (int)(vr.X * scaling + pad);
                                    int ty = (int)(vr.Y * scaling + pad);
                                    int tw = Math.Max(1, (int)(vr.Width * scaling - pad * 2));
                                    int th = Math.Max(1, (int)(vr.Height * scaling - pad * 2));
                                    Console.WriteLine($"[Spotify] Brute-force SetTileSize fallback: enumerating children of parentHwnd={parentHwnd} -> target {tx}x{ty}+{tw}x{th}");
                                    IntPtr child = FindWindowEx(parentHwnd, IntPtr.Zero, null, null);
                                    while (child != IntPtr.Zero)
                                    {
                                        try
                                        {
                                            SetWindowPos(child, IntPtr.Zero, tx, ty, tw, th, SWP_NOZORDER | SWP_NOACTIVATE);
                                        }
                                        catch { }
                                        child = FindWindowEx(parentHwnd, child, null, null);
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[Spotify] Brute-force SetTileSize fallback failed: {ex.Message}");
                            }
                         }
                     }
                     catch { }
                 }, DispatcherPriority.Background);
             }
             catch { }
        }

        private int _currentW = 2;
        private int _currentH = 3;
        private TopLevel? _topLevel;
        private bool _webViewCreated;
        private bool _isFrozen;

        // IInteractionFreezable — the dock settings panel is opening. The WebView must stay
        // VISIBLE at all times (both in normal dock mode and while settings is open).
        // Dragging is handled host-side via SharpHook (the global hook fires through the
        // WebView HWND), so we do NOT hide the HWND. We also keep the 33Hz continuous sync
        // running: it re-applies the WebView2 controller bounds + IsVisible=true every frame,
        // which is required because AvaloniaWebView's NativeControlHost otherwise re-asserts
        // stale (un-synced) bounds and the DComp surface goes black/misplaced.
        public void FreezeInteraction()
        {
            _isFrozen = true;
            if (_webView != null) _webView.IsVisible = true;
            StartContinuousSync();
        }

        public void UnfreezeInteraction()
        {
            _isFrozen = false;
            // If we skipped WebView creation because we were frozen before attach, create it now.
            if (_topLevel != null && _webView == null && !_webViewCreated)
            {
                _webViewCreated = true;
                TryCreateWebView();
            }
            if (_webView != null)
            {
                _webView.IsVisible = true;
                Dispatcher.UIThread.Post(() =>
                {
                    try { ForceWebViewRelayout(); SyncWebViewHwndToVisualBounds(); ApplyWebViewRoundedCorners(); }
                    catch { }
                }, DispatcherPriority.Background);
            }
            StartContinuousSync();
        }

        private const string SpotifyUrl = "https://open.spotify.com/";
        private const double WebViewPadding = 6.0;

        public spotify()
        {
            InitializeComponent();

            WidthMinus.Click  += (_, _) => Adjust(-1,  0);
            WidthPlus.Click   += (_, _) => Adjust(+1,  0);
            HeightMinus.Click += (_, _) => Adjust( 0, -1);
            HeightPlus.Click  += (_, _) => Adjust( 0, +1);

            SettingsButton.Click += (_, _) => ToggleSettings(true);
            CloseSettingsButton.Click += (_, _) => ToggleSettings(false);

            // Ctrl+scroll: drive the in-page scale-to-fit zoom. The in-page wheel listener
            // (ScaleToFitScript) handles the common case where the cursor is over the native
            // WebView HWND; this Avalonia-level handler is the fallback for when the wheel
            // event is captured by the Avalonia window chrome instead of the HWND.
            this.PointerWheelChanged += (_, e) =>
            {
                if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
                e.Handled = true;
                RunCoreScript("window.__docklyNudgeScale&&window.__docklyNudgeScale(" + (e.Delta.Y < 0 ? "-0.1" : "0.1") + ")");
            };

            RefreshSizeDisplay();
        }
        
        private void ToggleSettings(bool show)
        {
            SettingsOverlay.IsVisible = show;
            SettingsButton.IsVisible = !show;
            if (show)
            {
                if (_webView != null) _webView.IsVisible = false;
                if (OperatingSystem.IsWindows() && _webViewHwnd != IntPtr.Zero)
                    ShowWindow(_webViewHwnd, 0); // SW_HIDE
            }
            else
            {
                if (_webView != null) _webView.IsVisible = true;
                if (OperatingSystem.IsWindows() && _webViewHwnd != IntPtr.Zero)
                    ShowWindow(_webViewHwnd, 5); // SW_SHOW
                StartContinuousSync();
            }
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);

            _topLevel = TopLevel.GetTopLevel(this);

            // Native HWNDs ignore Avalonia render transforms. NativeControlHost positions
            // the HWND inside its ArrangeOverride (which uses TransformToVisual → includes
            // ancestor transforms). But ArrangeOverride only runs when *layout* is invalidated
            // — assigning RenderTransform invalidates visuals, not layout, so the HWND keeps
            // its previous (un-transformed) rect.
            //
            // Fix: when our RenderTransform changes (editor zoom slider) force an arrange
            // pass on the WebView's subtree. That re-runs NativeControlHost.ArrangeOverride
            // which calls TransformToVisual with the new scale, computing the correctly
            // scaled physical rect and positioning the HWND via ShowInClient. We then push
            // matching bounds into the WebView2 controller so its DComp surface fills the
            // resized host HWND.
            _ownRenderTransformSub ??= this.GetObservable(RenderTransformProperty)
                .Subscribe(new Avalonia.Reactive.AnonymousObserver<ITransform?>(_ =>
                    Dispatcher.UIThread.Post(ForceWebViewRelayout, DispatcherPriority.Background)));

            // Observe our own Bounds so we can react when the host changes the tile
            // size (some hosts update measure/arrange without changing RenderTransform).
            _boundsSub ??= this.GetObservable(BoundsProperty)
                .Subscribe(new Avalonia.Reactive.AnonymousObserver<Rect>(_ =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        try { SyncWebViewHwndToVisualBounds(); ApplyWebViewRoundedCorners(); }
                        catch { }
                    }, DispatcherPriority.Background);
                }));

            if (!_webViewCreated && !_isFrozen)
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
            DisposeAncestorSubs();
            _ownRenderTransformSub?.Dispose();
            _ownRenderTransformSub = null;
            _boundsSub?.Dispose();
            _boundsSub = null;
            _webViewBoundsSub?.Dispose();
            _webViewBoundsSub = null;
            _continuousSyncTimer?.Stop();
            _continuousSyncTimer = null;
            _shiftDown = false;
            _webViewHwnd = IntPtr.Zero;
        }

        // AvaloniaWebView's NativeControlHost re-asserts controller.Bounds to its own
        // (un-scaled) layout size on every layout/paint cycle, undoing our scaled set.
        // Event-driven syncing (LayoutUpdated, RenderTransform observers) loses the race
        // because AvaloniaWebView's handler runs after ours. Brute-force fix: poll at
        // ~30 Hz and keep re-applying the SCALED HWND size + WebView2 Bounds. The visual
        // result is the WebView following the editor's ScaleTransform, at the cost of a
        // bit of CPU. Active only while the module is attached to the visual tree.
        private DispatcherTimer? _continuousSyncTimer;
        private void StartContinuousSync()
        {
            if (_continuousSyncTimer != null) return;
            _continuousSyncTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            _continuousSyncTimer.Tick += (_, _) =>
            {
                SyncWebViewHwndToVisualBounds();
                ApplyWebViewRoundedCorners();
            };
            _continuousSyncTimer.Start();
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
                _webView = webView;

                // Also observe the created WebView's Bounds — LayoutUpdated might not
                // fire in all races, but Bounds observable reliably signals measured size changes.
                _webViewBoundsSub?.Dispose();
                _webViewBoundsSub = webView.GetObservable(BoundsProperty)
                    .Subscribe(new Avalonia.Reactive.AnonymousObserver<Rect>(_ =>
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            try { SyncWebViewHwndToVisualBounds(); ApplyWebViewRoundedCorners(); }
                            catch { }
                        }, DispatcherPriority.Background);
                    }));

                var urlProp = webViewType.GetProperty("Url",
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);

                webView.Loaded += (_, _) =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        SyncWebViewHwndToVisualBounds();
                        ApplyWebViewRoundedCorners();
                    }, DispatcherPriority.Background);
                    SubscribeToAncestorTransforms();
                    StartContinuousSync();
                    ScheduleInitialSettings();

                    try { urlProp?.SetValue(webView, new Uri(SpotifyUrl)); }
                    catch (Exception ex) { ShowError($"Failed to navigate to Spotify:\n{ex.InnerException?.Message ?? ex.Message}"); }

                    // Extra: ensure WebView2 controller receives initial bounds + zoom after load.
                    Dispatcher.UIThread.Post(() =>
                    {
                        try
                        {
                            if (_webView == null) return;
                            var top = TopLevel.GetTopLevel(_webView);
                            if (top == null) return;
                            var transform = _webView.TransformToVisual(top);
                            if (!transform.HasValue) return;
                            var local = _webView.Bounds;
                            if (local.Width <= 0 || local.Height <= 0) return;
                            var composed = transform.Value;
                            if (top.RenderTransform != null) composed = composed * top.RenderTransform.Value;
                            var visualRect = new Rect(0, 0, local.Width, local.Height).TransformToAABB(composed);
                            var scaling = top.RenderScaling;
                            double pad = WebViewPadding * scaling;
                            int w = Math.Max(1, (int)(visualRect.Width * scaling - pad * 2));
                            int h = Math.Max(1, (int)(visualRect.Height * scaling - pad * 2));
                            // Compute visual scale similar to SyncWebViewHwndToVisualBounds
                            double visualScale = 1.0;
                            try
                            {
                                var m = composed;
                                var p0 = m.Transform(new Point(0, 0));
                                var p1 = m.Transform(new Point(1, 0));
                                var p2 = m.Transform(new Point(0, 1));
                                double scaleX = Math.Sqrt(Math.Pow(p1.X - p0.X, 2) + Math.Pow(p1.Y - p0.Y, 2));
                                double scaleY = Math.Sqrt(Math.Pow(p2.X - p0.X, 2) + Math.Pow(p2.Y - p0.Y, 2));
                                if (double.IsFinite(scaleX) && double.IsFinite(scaleY) && scaleX > 0 && scaleY > 0)
                                    visualScale = (scaleX + scaleY) / 2.0;
                            }
                            catch { visualScale = 1.0; }

                            // Force controller reposition + zoom set
                            ForceWebView2RepositioningWithSize(0, w, h);
                        }
                        catch { }
                    }, DispatcherPriority.Background);
                };

                // Re-apply the rounded-corner clip + HWND sizing when the WebView resizes.
                // SyncWebViewHwndToVisualBounds computes the actual visual rect via
                // TransformToVisual so it correctly handles ancestor ScaleTransforms (editor zoom)
                // — unlike just using local Bounds * RenderScaling which ignores transforms.
                bool reapplyPending = false;
                webView.LayoutUpdated += (_, _) =>
                {
                    if (reapplyPending) return;
                    reapplyPending = true;
                    Dispatcher.UIThread.Post(() =>
                    {
                        reapplyPending = false;
                        SyncWebViewHwndToVisualBounds();
                        ApplyWebViewRoundedCorners();
                    }, DispatcherPriority.Background);
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

        // Live reference to the reflectively-created WebView control — used by
        // ForceWebView2Repositioning and the parent-transform subscription so the
        // WebView2 DComp surface stays glued to the Avalonia layout.
        private Control? _webView;

        private IDisposable? _ownRenderTransformSub;
        private IDisposable? _boundsSub;
        private IDisposable? _webViewBoundsSub;

        // Reflection/property caches used when driving WebView2 controller via reflection
        private System.Reflection.FieldInfo? _platformWebViewFieldCache;
        private System.Reflection.PropertyInfo? _coreControllerPropCache;
        private System.Reflection.PropertyInfo? _controllerBoundsPropCache;
        private System.Reflection.PropertyInfo? _controllerIsVisiblePropCache;
        private System.Reflection.MethodInfo? _notifyParentMovedMethodCache;

        // Win32 SetWindowPos and flags
        private const int SWP_NOZORDER = 0x0004;
        private const int SWP_NOACTIVATE = 0x0010;
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        // Subscriptions to every ancestor's RenderTransformProperty (catches transform
        // replacement, e.g. the editor's StackPanel.RenderTransform = new TranslateTransform()
        // when the user toggles slide preview). For each visual that currently holds a
        // TranslateTransform, _ancestorInnerSubs has a nested subscription on its X/Y so
        // tick-by-tick animation changes also fire OnAncestorTransformChanged.
        private readonly List<IDisposable> _ancestorOuterSubs = new();
        private readonly Dictionary<Visual, IDisposable> _ancestorInnerSubs = new();
        private bool _syncPending;

        // Walk from this UserControl up to the TopLevel and subscribe to every ancestor's
        // RenderTransform so the WebView HWND follows whichever ancestor is sliding.
        //
        // In the main Docklys app the MainWindow itself (the TopLevel) holds the slide
        // TranslateTransform; in the editor it's an intermediate StackPanel. Subscribing
        // to every ancestor handles both, plus any future host that animates a different
        // ancestor.
        private void SubscribeToAncestorTransforms()
        {
            DisposeAncestorSubs();

            var topLevel = TopLevel.GetTopLevel(this);
            Visual? v = this;
            while (v != null)
            {
                var visual = v;
                _ancestorOuterSubs.Add(visual.GetObservable(Visual.RenderTransformProperty)
                    .Subscribe(new Avalonia.Reactive.AnonymousObserver<ITransform?>(t =>
                    {
                        WireUpInnerTransform(visual, t);
                        OnAncestorTransformChanged();
                    })));

                if (v == topLevel) break;
                v = v.GetVisualParent();
            }
        }

        private void WireUpInnerTransform(Visual visual, ITransform? transform)
        {
            if (_ancestorInnerSubs.TryGetValue(visual, out var oldSub))
            {
                oldSub.Dispose();
                _ancestorInnerSubs.Remove(visual);
            }

            if (transform is TranslateTransform translate)
            {
                var xSub = translate.GetObservable(TranslateTransform.XProperty)
                    .Subscribe(new Avalonia.Reactive.AnonymousObserver<double>(_ => OnAncestorTransformChanged()));
                var ySub = translate.GetObservable(TranslateTransform.YProperty)
                    .Subscribe(new Avalonia.Reactive.AnonymousObserver<double>(_ => OnAncestorTransformChanged()));
                _ancestorInnerSubs[visual] = new CompositeDisposable(xSub, ySub);
            }
        }

        // Coalesce repeated transform notifications (many fire per frame during a slide)
        // into a single SyncWebViewHwndToVisualBounds call per dispatcher cycle.
        private void OnAncestorTransformChanged()
        {
            if (_syncPending) return;
            _syncPending = true;
            Dispatcher.UIThread.Post(() =>
            {
                _syncPending = false;
                SyncWebViewHwndToVisualBounds();
            }, DispatcherPriority.Render);
        }

        private void DisposeAncestorSubs()
        {
            foreach (var sub in _ancestorOuterSubs) sub.Dispose();
            _ancestorOuterSubs.Clear();
            foreach (var sub in _ancestorInnerSubs.Values) sub.Dispose();
            _ancestorInnerSubs.Clear();
        }

        private sealed class CompositeDisposable : IDisposable
        {
            private readonly IDisposable[] _items;
            public CompositeDisposable(params IDisposable[] items) { _items = items; }
            public void Dispose() { foreach (var d in _items) d.Dispose(); }
        }

        // The NativeControlHost HWND that Avalonia created for this instance's WebView.
        // Looked up once via CoreWebView2Controller.ParentWindow so that HWND-level
        // operations (SetWindowPos, SetWindowRgn) target only this instance's HWND
        // instead of all children of the editor window (which breaks dual-view).
        private IntPtr _webViewHwnd = IntPtr.Zero;
        private bool _shiftDown = false;
        private void OnTopLevelKeyDown(object? s, Avalonia.Input.KeyEventArgs e) { if (e.Key == Avalonia.Input.Key.LeftShift || e.Key == Avalonia.Input.Key.RightShift) _shiftDown = true; }
        private void OnTopLevelKeyUp(object? s, Avalonia.Input.KeyEventArgs e)   { if (e.Key == Avalonia.Input.Key.LeftShift || e.Key == Avalonia.Input.Key.RightShift) _shiftDown = false; }
        // Cache last applied visual zoom to avoid redundant sets and reduce races.
        // Start as NaN so the first set always applies.
        private double _lastAppliedVisualZoom = double.NaN;
        // Visible (clipped) HWND width/height in physical pixels. The HWND is made wider/taller
        // by the Chromium scrollbar width so the scrollbars render off-screen; the
        // SetWindowRgn clip is set to these narrower values to hide those extra pixels.
        private int _visibleHwndW;
        private int _visibleHwndH;
        // Mobile user agent so Spotify serves the compact mobile layout.
        private const string MobileUserAgent =
            "Mozilla/5.0 (Linux; Android 13; Pixel 7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/116.0.0.0 Mobile Safari/537.36";
        private bool _mobileUaApplied;
        // Pending zoom retry state when the controller isn't ready yet.
        private DispatcherTimer? _zoomRetryTimer;
        private double? _pendingZoomVal;
        private int _pendingZoomW;
        private int _pendingZoomH;
        private int _pendingZoomAttempts;

        // Applies the mobile user agent and initial page zoom directly via the
        // CoreWebView2 controller. Called from the WebView Loaded event and retried
        // until CoreWebView2 is ready, so it works even when the controller isn't
        // available immediately (e.g. in the main Docklys app).
        // Injected into the WebView page to implement "scale-to-fit" Ctrl+scroll zoom.
        // Instead of WebView2's built-in ZoomFactor (which REFLOWS responsive pages and trips
        // their desktop breakpoint — widening the layout past the tile so it gets clipped at
        // the sides), this applies a uniform CSS transform to <html>. Width/height are
        // compensated by 1/scale so the scaled visual always fills exactly 100% of the
        // viewport: content is never clipped at the sides in either zoom direction, and media
        // queries still see the real (narrow) viewport so the mobile layout is preserved.
        // The capture-phase wheel listener calls preventDefault() so WebView2's native zoom
        // never fires. Guarded by __docklyScaleReady so repeated injection is a no-op.
        private const string ScaleToFitScript = @"
(function(){
    if (window.__docklyScaleReady) return;
    window.__docklyScaleReady = true;
    if (typeof window.__docklyScale !== 'number') window.__docklyScale = 1;
    function apply(){
        var de = document.documentElement;
        if(!de) return;
        var s = window.__docklyScale;
        if (s === 1){
            de.style.transform = '';
            de.style.transformOrigin = '';
            de.style.width = '';
            de.style.height = '';
        } else {
            de.style.transformOrigin = '0 0';
            de.style.transform = 'scale(' + s + ')';
            de.style.width = (100 / s) + '%';
            de.style.height = (100 / s) + '%';
        }
    }
    window.__docklyApplyScale = apply;
    window.__docklyNudgeScale = function(d){
        var s = (window.__docklyScale || 1) + d;
        if (s < 0.25) s = 0.25;
        if (s > 3) s = 3;
        window.__docklyScale = Math.round(s * 100) / 100;
        apply();
    };
    window.addEventListener('wheel', function(e){
        if(!e.ctrlKey) return;
        e.preventDefault();
        e.stopPropagation();
        window.__docklyNudgeScale(e.deltaY < 0 ? 0.1 : -0.1);
    }, { passive:false, capture:true });
    window.addEventListener('keydown', function(e){
        if(!e.ctrlKey) return;
        if(e.key === '+' || e.key === '='){ e.preventDefault(); window.__docklyNudgeScale(0.1); }
        else if(e.key === '-' || e.key === '_'){ e.preventDefault(); window.__docklyNudgeScale(-0.1); }
        else if(e.key === '0'){ e.preventDefault(); window.__docklyScale = 1; apply(); }
    }, { capture:true });
    setInterval(apply, 500);
})();
";

        // Executes arbitrary JS in the WebView2 page via reflection. Used to drive the
        // in-page scale-to-fit zoom from Avalonia-level input when the wheel event is
        // captured by the Avalonia window instead of the native WebView HWND.
        private void RunCoreScript(string js)
        {
            if (_webView == null) return;
            try
            {
                _platformWebViewFieldCache ??= _webView.GetType().GetField(
                    "_platformWebView", BindingFlags.NonPublic | BindingFlags.Instance);
                if (_platformWebViewFieldCache == null) return;
                var platformWebView = _platformWebViewFieldCache.GetValue(_webView);
                if (platformWebView == null) return;
                var platformType = platformWebView.GetType();
                if (_coreControllerPropCache == null || _coreControllerPropCache.DeclaringType != platformType)
                    _coreControllerPropCache = platformType.GetProperty(
                        "_coreWebView2Controller", BindingFlags.NonPublic | BindingFlags.Instance);
                if (_coreControllerPropCache == null) return;
                var controller = _coreControllerPropCache.GetValue(platformWebView);
                if (controller == null) return;
                var core = controller.GetType().GetProperty("CoreWebView2")?.GetValue(controller);
                if (core == null) return;
                var exec = core.GetType().GetMethod("ExecuteScriptAsync", new[] { typeof(string) });
                exec?.Invoke(core, new object[] { js });
            }
            catch { }
        }

        private void ScheduleInitialSettings()
        {
            _mobileUaApplied = false;
            var attempts = 0;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            timer.Tick += (_, _) =>
            {
                attempts++;
                if (TryApplyInitialSettings() || attempts >= 20)
                    timer.Stop();
            };
            Dispatcher.UIThread.Post(() => TryApplyInitialSettings(), DispatcherPriority.Background);
            timer.Start();
        }

        private bool TryApplyInitialSettings()
        {
            if (_webView == null) return false;
            try
            {
                _platformWebViewFieldCache ??= _webView.GetType().GetField(
                    "_platformWebView", BindingFlags.NonPublic | BindingFlags.Instance);
                if (_platformWebViewFieldCache == null) return false;
                var platformWebView = _platformWebViewFieldCache.GetValue(_webView);
                if (platformWebView == null) return false;

                var platformType = platformWebView.GetType();
                if (_coreControllerPropCache == null || _coreControllerPropCache.DeclaringType != platformType)
                    _coreControllerPropCache = platformType.GetProperty(
                        "_coreWebView2Controller", BindingFlags.NonPublic | BindingFlags.Instance);
                if (_coreControllerPropCache == null) return false;
                var controller = _coreControllerPropCache.GetValue(platformWebView);
                if (controller == null) return false;
                var controllerType = controller.GetType();

                // Get CoreWebView2 — not ready yet if null, retry later.
                var coreProp = controllerType.GetProperty("CoreWebView2");
                var core = coreProp?.GetValue(controller);
                if (core == null) return false;

                var coreType = core.GetType();

                if (!_mobileUaApplied)
                {
                    try
                    {
                        // Set initial page zoom to 50% (5 Ctrl+scroll-down notches: 100→90→80→75→67→50).
                        // Done once here; the sync timer never touches ZoomFactor so Ctrl+scroll works freely.
                        var zoomProp = controllerType.GetProperty("ZoomFactor");
                        if (zoomProp?.PropertyType == typeof(double))
                            try { zoomProp.SetValue(controller, 0.5); } catch { }

                        var settingsProp = coreType.GetProperty("Settings");
                        var settings = settingsProp?.GetValue(core);
                        if (settings != null)
                        {
                            // Method 3: Disable native scrollbars at the engine level.
                            try
                            {
                                var scrollEnabledProp = settings.GetType().GetProperty("IsScrollEnabled");
                                if (scrollEnabledProp?.PropertyType == typeof(bool))
                                    scrollEnabledProp.SetValue(settings, false);
                            }
                            catch { }

                            var uaProp = settings.GetType().GetProperty("UserAgent");
                            if (uaProp != null && !_mobileUaApplied)
                            {
                                uaProp.SetValue(settings, MobileUserAgent);
                                _mobileUaApplied = true;

                                // Hide scrollbars persistently. Spotify is a React SPA that
                                // rewrites <head>/<body> on navigation (e.g. opening settings),
                                // wiping any injected <style>. A setInterval re-appends our style
                                // every 500ms so the hidden-scrollbar CSS survives DOM rewrites.
                                // Registered via AddScriptToExecuteOnDocumentCreatedAsync so it
                                // runs for every future document. NOTE: we only hide scrollbars
                                // (no overflow:hidden/position:fixed) so the page still scrolls
                                // and zooming does not clip content.
                                const string persistentScript = @"
(function(){
    const css = '* { scrollbar-width: none !important; -ms-overflow-style: none !important; }'
        + '::-webkit-scrollbar, *::-webkit-scrollbar { display: none !important; width: 0px !important; height: 0px !important; }'
        + '::-webkit-scrollbar-thumb, *::-webkit-scrollbar-thumb { display: none !important; }'
        + '::-webkit-scrollbar-track, *::-webkit-scrollbar-track { display: none !important; }';
    const style = document.createElement('style');
    style.id = 'persistent-hide-scroll';
    style.appendChild(document.createTextNode(css));
    function enforceScrollbarRemoval() {
        if (!document.getElementById('persistent-hide-scroll')) {
            if (document.head) { document.head.appendChild(style); }
            else if (document.body) { document.body.appendChild(style); }
        }
    }
    enforceScrollbarRemoval();
    setInterval(enforceScrollbarRemoval, 500);
})();
";
                                var addScript = coreType.GetMethod("AddScriptToExecuteOnDocumentCreatedAsync",
                                    new[] { typeof(string) });
                                addScript?.Invoke(core, new object[] { persistentScript });
                                addScript?.Invoke(core, new object[] { ScaleToFitScript });

                                // Navigate with the new UA.
                                var navigateMethod = coreType.GetMethod("Navigate", new[] { typeof(string) });
                                navigateMethod?.Invoke(core, new object[] { SpotifyUrl });
                            }
                        }
                    }
                    catch { }
                }

                // Inject into the live page on every retry tick as well — catches the window
                // between document creation and our MutationObserver being registered.
                try
                {
                    const string liveScript =
                        "(function(){" +
                        "const css='* { scrollbar-width: none !important; -ms-overflow-style: none !important; } " +
                        "::-webkit-scrollbar, *::-webkit-scrollbar { display: none !important; width: 0px !important; height: 0px !important; background: transparent !important; } " +
                        "::-webkit-scrollbar-thumb, *::-webkit-scrollbar-thumb { display: none !important; background: transparent !important; } " +
                        "::-webkit-scrollbar-track, *::-webkit-scrollbar-track { display: none !important; background: transparent !important; }';" +
                        "function apply(){" +
                        "let s=document.getElementById('_dockly_noscroll');" +
                        "if(!s){s=document.createElement('style');s.id='_dockly_noscroll';" +
                        "(document.head||document.documentElement).appendChild(s);}" +
                        "if(s.textContent!==css)s.textContent=css;" +
                        "}" +
                        "apply();" +
                        "if(!window._docklyObserver){" +
                        "window._docklyObserver=new MutationObserver(apply);" +
                        "window._docklyObserver.observe(document.documentElement,{childList:true,subtree:true,attributes:true});}" +
                        "})();";
                    var execScript = coreType.GetMethod("ExecuteScriptAsync", new[] { typeof(string) });
                    execScript?.Invoke(core, new object[] { liveScript });
                    execScript?.Invoke(core, new object[] { ScaleToFitScript });
                }
                catch { }

                return _mobileUaApplied;
            }
            catch { return false; }
        }

        private void StartZoomRetry(double zoomVal, int w, int h)
        {
            _pendingZoomVal = zoomVal;
            _pendingZoomW = w;
            _pendingZoomH = h;
            _pendingZoomAttempts = 0;
            if (_zoomRetryTimer == null)
            {
                _zoomRetryTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
                _zoomRetryTimer.Tick += (_, _) =>
                {
                    _pendingZoomAttempts++;
                    if (_pendingZoomAttempts > 10)
                    {
                        StopZoomRetry();
                        return;
                    }
                    // Try to re-apply with the latest pending values
                    try
                    {
                        if (_pendingZoomVal.HasValue)
                            ForceWebView2RepositioningWithSize(0, _pendingZoomW, _pendingZoomH, _pendingZoomVal);
                    }
                    catch { }
                };
            }
            _zoomRetryTimer.Start();
        }

        private void StopZoomRetry()
        {
            try
            {
                _zoomRetryTimer?.Stop();
                _zoomRetryTimer = null;
            }
            catch { }
            _pendingZoomVal = null;
            _pendingZoomAttempts = 0;
        }

        // Directly drives the WebView2 CoreWebView2Controller's Bounds and pings
        // NotifyParentWindowPositionChanged, bypassing Avalonia's NativeControlHost
        // rect cache (which silently skips repositioning when the inner control's
        // local bounds appear unchanged — e.g. during a parent RenderTransform slide).
        // Mirrors SettingsWindow.ForceWebView2Repositioning.
        private bool ForceWebView2Repositioning(int physicalOffsetX = 0)
        {
            if (_webView == null) return false;

            try
            {
                _platformWebViewFieldCache ??= _webView.GetType().GetField(
                    "_platformWebView", BindingFlags.NonPublic | BindingFlags.Instance);
                // Heuristic: if the standard field name isn't present, search for a field
                // whose type name contains 'Platform' or 'PlatformWebView' or which
                // exposes a _coreWebView2Controller property.
                if (_platformWebViewFieldCache == null)
                {
                    foreach (var f in _webView.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                    {
                        try
                        {
                            var ft = f.FieldType;
                            if (ft.Name.IndexOf("Platform", StringComparison.OrdinalIgnoreCase) >= 0 || ft.Name.IndexOf("AvaloniaWebView", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                // quick check: does this field's type have a _coreWebView2Controller property?
                                var p = ft.GetProperty("_coreWebView2Controller", BindingFlags.NonPublic | BindingFlags.Instance);
                                if (p != null)
                                {
                                    _platformWebViewFieldCache = f;
                                    break;
                                }
                            }
                        }
                        catch { }
                    }
                }
                if (_platformWebViewFieldCache == null) return false;

                var platformWebView = _platformWebViewFieldCache.GetValue(_webView);
                if (platformWebView == null) return false;

                var platformType = platformWebView.GetType();
                if (_coreControllerPropCache == null || _coreControllerPropCache.DeclaringType != platformType)
                {
                    _coreControllerPropCache = platformType.GetProperty(
                        "_coreWebView2Controller", BindingFlags.NonPublic | BindingFlags.Instance);
                    // Heuristic: if not found, look for any property whose type name contains 'CoreWebView2Controller' or name contains 'core' and 'controller'
                    if (_coreControllerPropCache == null)
                    {
                        foreach (var pp in platformType.GetProperties(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                        {
                            try
                            {
                                var pt = pp.PropertyType;
                                if (pt != null && pt.Name.IndexOf("CoreWebView2Controller", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    _coreControllerPropCache = pp;
                                    break;
                                }
                                var nm = pp.Name.ToLowerInvariant();
                                if (nm.Contains("core") && nm.Contains("controller"))
                                {
                                    _coreControllerPropCache = pp;
                                    break;
                                }
                            }
                            catch { }
                        }
                    }
                }
                if (_coreControllerPropCache == null) return false;

                var controller = _coreControllerPropCache.GetValue(platformWebView);
                if (controller == null) return false;

                var controllerType = controller.GetType();
                var bounds = _webView.Bounds;
                var topLevel = TopLevel.GetTopLevel(_webView);
                var scaling = topLevel?.RenderScaling ?? 1.0;
                int w = Math.Max(1, (int)(bounds.Width  * scaling));
                int h = Math.Max(1, (int)(bounds.Height * scaling));
                var rect = new System.Drawing.Rectangle(physicalOffsetX, 0, w, h);

                _controllerBoundsPropCache    ??= controllerType.GetProperty("Bounds");
                _controllerBoundsPropCache?.SetValue(controller, rect);

                _controllerIsVisiblePropCache ??= controllerType.GetProperty("IsVisible");
                _controllerIsVisiblePropCache?.SetValue(controller, true);

                _notifyParentMovedMethodCache ??= controllerType.GetMethod("NotifyParentWindowPositionChanged");
                _notifyParentMovedMethodCache?.Invoke(controller, null);

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Spotify] ForceWebView2Repositioning failed: {ex.Message}");
                return false;
            }
        }

        // The WebView2 control is hosted in a native child HWND that Avalonia's compositor
        // can't clip — ClipToBounds and CornerRadius on the wrapping Border don't reach it.
        // The only reliable fix on Windows is SetWindowRgn on the child HWND itself, which is
        // the same trick the main Dockly app uses for its SettingsWindow marketplace WebView.
        // Mirrors PlatformWindowService.RoundChildWindowCorners from the main app.
        private void ApplyWebViewRoundedCorners()
        {
            if (!OperatingSystem.IsWindows()) return;

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;
            var parentHwnd = topLevel.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (parentHwnd == IntPtr.Zero) return;

            double scaling = topLevel.RenderScaling;

            // Match the outer Avalonia Border's visual radius: it's CornerRadius=10 in
            // logical units. Since the WebView is inset by WebViewPadding=6 (creating
            // a uniform gap on all sides), we use a smaller concentric radius of 4
            // for the inner WebView HWND to look correct (10 - 6 = 4).
            double visualScale = 1.0;
            var cornerTransform = this.TransformToVisual(topLevel);
            if (cornerTransform.HasValue)
            {
                var m = cornerTransform.Value;
                if (topLevel.RenderTransform != null)
                {
                    try { m = cornerTransform.Value * topLevel.RenderTransform.Value; } catch { }
                }
                var p0 = m.Transform(new Point(0, 0));
                var p1 = m.Transform(new Point(1, 0));
                var p2 = m.Transform(new Point(0, 1));
                double sx = Math.Sqrt(Math.Pow(p1.X - p0.X, 2) + Math.Pow(p1.Y - p0.Y, 2));
                double sy = Math.Sqrt(Math.Pow(p2.X - p0.X, 2) + Math.Pow(p2.Y - p0.Y, 2));
                if (double.IsFinite(sx) && double.IsFinite(sy) && sx > 0 && sy > 0)
                    visualScale = (sx + sy) / 2.0;
            }

            int diameter = Math.Max(2, (int)(4 * scaling * visualScale * 2));

            try
            {
                var innerHwnd = TryGetWebViewHwnd();
                if (innerHwnd != IntPtr.Zero)
                {
                    // Walk up to the wrapper HWND whose parent is the TopLevel. The wrapper
                    // can paint over the inner's rounded clip if it isn't itself clipped,
                    // so apply the region to BOTH when they differ.
                    IntPtr outerHwnd = innerHwnd;
                    try
                    {
                        while (true)
                        {
                            var parent = GetParent(outerHwnd);
                            if (parent == IntPtr.Zero || parent == parentHwnd) break;
                            outerHwnd = parent;
                        }
                    }
                    catch { }

                    ApplyRoundedRgn(outerHwnd, diameter, _visibleHwndW, _visibleHwndH);
                    if (outerHwnd != innerHwnd) ApplyRoundedRgn(innerHwnd, diameter, _visibleHwndW, _visibleHwndH);
                }
                else
                {
                    // Fallback when HWND isn't known yet.
                    IntPtr child = FindWindowEx(parentHwnd, IntPtr.Zero, null, null);
                    while (child != IntPtr.Zero)
                    {
                        ApplyRoundedRgn(child, diameter, _visibleHwndW, _visibleHwndH);
                        child = FindWindowEx(parentHwnd, child, null, null);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Spotify] ApplyWebViewRoundedCorners failed: {ex.Message}");
            }
        }

        private static void ApplyRoundedRgn(IntPtr hwnd, int diameter, int visibleWidth = 0, int visibleHeight = 0)
        {
            if (!GetClientRect(hwnd, out RECT r)) return;
            // Use visibleWidth/Height when supplied so the clip excludes the off-screen scrollbar pixels.
            int w = (visibleWidth > 0 && visibleWidth < r.Right - r.Left) ? visibleWidth : (r.Right - r.Left);
            int h = (visibleHeight > 0 && visibleHeight < r.Bottom - r.Top) ? visibleHeight : (r.Bottom - r.Top);
            if (w <= 50 || h <= 50) return;
            IntPtr rgn = CreateRoundRectRgn(0, 0, w + 1, h + 1, diameter, diameter);
            SetWindowRgn(hwnd, rgn, true);
        }

        // Two-step relayout when a parent RenderTransform changes:
        //   1. Walk up from the WebView invalidating arrange on every visual until
        //      the TopLevel. That re-runs NativeControlHost.ArrangeOverride which
        //      calls TransformToVisual(topLevel) → includes the new RenderTransform
        //      → physical pixel rect → ShowInClient → HWND is moved/resized.
        //   2. After the layout pass settles, run the SetWindowPos + controller-Bounds
        //      sync as a belt-and-suspenders override (in case Avalonia's
        //      NativeControlHost implementation skips ancestor transforms).
        private void ForceWebViewRelayout()
        {
            Console.WriteLine("[Spotify] ForceWebViewRelayout() called");
            if (_webView == null) return;

            var topLevel = TopLevel.GetTopLevel(_webView);
            if (topLevel == null) return;

            try
            {
                Visual? v = _webView;
                while (v != null && v != topLevel)
                {
                    if (v is Layoutable l)
                    {
                        l.InvalidateMeasure();
                        l.InvalidateArrange();
                    }
                    v = v.GetVisualParent();
                }
                (topLevel as Layoutable)?.InvalidateArrange();
                if (_webView.IsVisible)
                {
                    topLevel.UpdateLayout();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Spotify] ForceWebViewRelayout handled exception: {ex.Message}");
            }

            // Belt-and-suspenders: also drive the HWND + WebView2 controller bounds
            // directly. Defer one frame so it runs after the just-triggered arrange pass.
            Dispatcher.UIThread.Post(SyncWebViewHwndToVisualBounds, DispatcherPriority.Background);
        }

        // Native HWNDs ignore Avalonia render transforms — when an ancestor applies a
        // ScaleTransform (editor zoom slider, host tile-resize) the WebView host HWND
        // stays at its un-scaled 230×350. Manually SetWindowPos it to the actual
        // transformed visual rect, then push matching bounds into the WebView2 controller
        // so its DComp surface fills the new HWND area.
        private void SyncWebViewHwndToVisualBounds()
        {
            if (!OperatingSystem.IsWindows() || _webView == null) return;

            var topLevel = TopLevel.GetTopLevel(_webView);
            if (topLevel == null) return;
            var parentHwnd = topLevel.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (parentHwnd == IntPtr.Zero) return;

            var transform = _webView.TransformToVisual(topLevel);
            if (!transform.HasValue) return;
            var local = _webView.Bounds;
            if (local.Width <= 0 || local.Height <= 0) return;

            Console.WriteLine($"[Spotify] SyncWebViewHwndToVisualBounds: localBounds={local.Width}x{local.Height}");

            // TransformToVisual returns local->topLevel excluding the TopLevel's own RenderTransform.
            // Compose it after (right-multiply) so the full local→visual pipeline is correct.
            // In Avalonia's convention, A*B applies A first then B, so:
            //   transform.Value * topLevel.RenderTransform.Value
            //   = (local→topLevel layout) then (topLevel layout→visual)
            // The wrong order (topLevel.RenderTransform * transform) would apply the window
            // translation to local coords before the Viewbox scale, shrinking the offset by
            // the Viewbox scale factor and producing a left-shifted HWND.
            var composedMatrix = transform.Value;
            if (topLevel.RenderTransform != null)
            {
                try { composedMatrix = transform.Value * topLevel.RenderTransform.Value; }
                catch { composedMatrix = transform.Value; }
            }

            // Compute visual scale from the composed matrix to ensure padding and
            // corner radius scale proportionally with the module's visual size.
            double visualScale = 1.0;
            try
            {
                var m = composedMatrix;
                var p0 = m.Transform(new Point(0, 0));
                var p1 = m.Transform(new Point(1, 0));
                var p2 = m.Transform(new Point(0, 1));
                double scaleX = Math.Sqrt(Math.Pow(p1.X - p0.X, 2) + Math.Pow(p1.Y - p0.Y, 2));
                double scaleY = Math.Sqrt(Math.Pow(p2.X - p0.X, 2) + Math.Pow(p2.Y - p0.Y, 2));
                if (double.IsFinite(scaleX) && double.IsFinite(scaleY) && scaleX > 0 && scaleY > 0)
                    visualScale = (scaleX + scaleY) / 2.0;
            }
            catch { visualScale = 1.0; }

            var visualRect = new Rect(0, 0, local.Width, local.Height).TransformToAABB(composedMatrix);
            var scaling = topLevel.RenderScaling;

            // Apply WebViewPadding (fixed gap on all sides). Scale the padding by 
            // visualScale so the gap remains proportional to the module's zoomed size.
            double pad = WebViewPadding * scaling * visualScale;
            int x = (int)(visualRect.X * scaling + pad);
            int y = (int)(visualRect.Y * scaling + pad);
            int w = Math.Max(1, (int)(visualRect.Width * scaling - pad * 2));
            int h = Math.Max(1, (int)(visualRect.Height * scaling - pad * 2));

            // Extend the HWND by the Chromium scrollbar width so the scrollbars render
            // off the right/bottom edge. SetWindowRgn (via ApplyWebViewRoundedCorners) clips
            // the visible region back to w,making the scrollbar invisible.
            int scrollbarExtra = Math.Max(0, (int)(15 * scaling));
            _visibleHwndW = w;
            _visibleHwndH = h;
            int wExt = w + scrollbarExtra;
            int hExt = h + scrollbarExtra;

            int targetX = x;

            try
            {
                var ourHwnd = TryGetWebViewHwnd();
                Console.WriteLine($"[Spotify] TryGetWebViewHwnd returned {ourHwnd}");
                Console.WriteLine($"[Spotify] visualRect (shrunk)={x},{y},{w},{h} renderScaling={scaling}");

                if (ourHwnd != IntPtr.Zero)
                {
                    // Move the outermost native child that is still a descendant of the TopLevel.
                    IntPtr outerHwnd = ourHwnd;
                    try
                    {
                        while (true)
                        {
                            var parent = GetParent(outerHwnd);
                            if (parent == IntPtr.Zero || parent == parentHwnd) break;
                            outerHwnd = parent;
                        }
                    }
                    catch { /* ignore */ }

                    // Position the outer window (the one whose origin is in TopLevel coords).
                    // Use wExt/hExt so the scrollbars render off the edge (clipped by SetWindowRgn).
                    SetWindowPos(outerHwnd, IntPtr.Zero, targetX, y, wExt, hExt, SWP_NOZORDER | SWP_NOACTIVATE);

                    // If we moved a wrapper (outer != inner), ensure the inner WebView HWND
                    // fills the moved container so the DComp surface is at 0,0..wExt,hExt.
                    if (outerHwnd != ourHwnd)
                    {
                        SetWindowPos(ourHwnd, IntPtr.Zero, 0, 0, wExt, hExt, SWP_NOZORDER | SWP_NOACTIVATE);
                        Console.WriteLine($"[Spotify] Positioned outerHwnd={outerHwnd} innerHwnd={ourHwnd} -> x={targetX},y={y},w={wExt},h={hExt}");
                    }
                }
                else
                {
                    IntPtr child = FindWindowEx(parentHwnd, IntPtr.Zero, null, null);
                    while (child != IntPtr.Zero)
                    {
                        if (GetClientRect(child, out RECT r))
                        {
                            int cw = r.Right - r.Left;
                            int ch = r.Bottom - r.Top;
                            Console.WriteLine($"[Spotify] Enumerating child HWND={child} client={cw}x{ch}");
                            if (cw > 50 && ch > 50)
                                SetWindowPos(child, IntPtr.Zero, targetX, y, wExt, hExt,
                                    SWP_NOZORDER | SWP_NOACTIVATE);
                        }
                        child = FindWindowEx(parentHwnd, child, null, null);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Spotify] SyncWebViewHwndToVisualBounds failed: {ex.Message}");
            }

            // HWND is now at the correct slid position — DComp surface fills it from (0,0).
            // Compute visual scale using the transform matrix to be robust to rotations/shear.
            visualScale = 1.0;
            try
            {
                // Use the composed matrix (local -> topLevel including TopLevel.RenderTransform)
                var m = composedMatrix;
                 // Transform unit vectors to measure scale along X and Y
                 var p0 = m.Transform(new Point(0, 0));
                 var p1 = m.Transform(new Point(1, 0));
                 var p2 = m.Transform(new Point(0, 1));
                 double scaleX = Math.Sqrt(Math.Pow(p1.X - p0.X, 2) + Math.Pow(p1.Y - p0.Y, 2));
                 double scaleY = Math.Sqrt(Math.Pow(p2.X - p0.X, 2) + Math.Pow(p2.Y - p0.Y, 2));
                 if (double.IsFinite(scaleX) && double.IsFinite(scaleY) && scaleX > 0 && scaleY > 0)
                     visualScale = (scaleX + scaleY) / 2.0;
            }
            catch { visualScale = 1.0; }

            Console.WriteLine($"[Spotify] computed visualScale={visualScale}");

            ForceWebView2RepositioningWithSize(0, wExt, hExt);

        }

        // Same as ForceWebView2Repositioning but with explicit width/height so we can
        // pass the SCALED size (accounting for ancestor ScaleTransform) instead of the
        // un-scaled local Bounds * RenderScaling that the base overload uses.
        private bool ForceWebView2RepositioningWithSize(int physicalOffsetX, int w, int h, double? zoom = null)
         {
             if (_webView == null) return false;
             try
             {
                 _platformWebViewFieldCache ??= _webView.GetType().GetField(
                     "_platformWebView", BindingFlags.NonPublic | BindingFlags.Instance);
                 // Heuristic: if the standard field name isn't present, search for a field
                 // whose type name contains 'Platform' or 'PlatformWebView' or which
                 // exposes a _coreWebView2Controller property.
                 if (_platformWebViewFieldCache == null)
                 {
                     foreach (var f in _webView.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                     {
                         try
                         {
                             var ft = f.FieldType;
                             if (ft.Name.IndexOf("Platform", StringComparison.OrdinalIgnoreCase) >= 0 || ft.Name.IndexOf("AvaloniaWebView", StringComparison.OrdinalIgnoreCase) >= 0)
                             {
                                 // quick check: does this field's type have a _coreWebView2Controller property?
                                 var p = ft.GetProperty("_coreWebView2Controller", BindingFlags.NonPublic | BindingFlags.Instance);
                                 if (p != null)
                                 {
                                     _platformWebViewFieldCache = f;
                                     break;
                                 }
                             }
                         }
                         catch { }
                     }
                 }
                 if (_platformWebViewFieldCache == null) return false;
                 var platformWebView = _platformWebViewFieldCache.GetValue(_webView);
                 if (platformWebView == null) return false;
                 var platformType = platformWebView.GetType();
                 if (_coreControllerPropCache == null || _coreControllerPropCache.DeclaringType != platformType)
                     _coreControllerPropCache = platformType.GetProperty("_coreWebView2Controller",
                         BindingFlags.NonPublic | BindingFlags.Instance);
                 if (_coreControllerPropCache == null) return false;
                 var controller = _coreControllerPropCache.GetValue(platformWebView);
                 if (controller == null) return false;
                 var controllerType = controller.GetType();

                 var rect = new System.Drawing.Rectangle(physicalOffsetX, 0, w, h);
                Console.WriteLine($"[Spotify] ForceWebView2RepositioningWithSize rect={{X={rect.X},Y={rect.Y},W={rect.Width},H={rect.Height}}} zoom={zoom}");
                 _controllerBoundsPropCache    ??= controllerType.GetProperty("Bounds");
                 _controllerBoundsPropCache?.SetValue(controller, rect);

                 _controllerIsVisiblePropCache ??= controllerType.GetProperty("IsVisible");
                 _controllerIsVisiblePropCache?.SetValue(controller, true);
                 _notifyParentMovedMethodCache ??= controllerType.GetMethod("NotifyParentWindowPositionChanged");
                 _notifyParentMovedMethodCache?.Invoke(controller, null);

                 return true;
             }
             catch (Exception ex)
             {
                 Console.WriteLine($"[Spotify] ForceWebView2RepositioningWithSize failed: {ex.Message}");
                 return false;
             }
         }

        // Returns the NativeControlHost HWND for this instance's WebView by reading
        // CoreWebView2Controller.ParentWindow via reflection. Cached after first lookup.
        // This lets SyncWebViewHwndToVisualBounds and ApplyWebViewRoundedCorners target
        // only our HWND instead of all child HWNDs — which is wrong in dual-view mode
        // where two WebView HWNDs both exist as children of the same editor window.
        private IntPtr TryGetWebViewHwnd()
        {
            // If we already cached an HWND, ensure it's still valid (hasn't been reparented)
            try
            {
                if (_webViewHwnd != IntPtr.Zero && _webView != null)
                {
                    var topLevel = TopLevel.GetTopLevel(_webView);
                    var parentHwnd = topLevel?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
                    if (parentHwnd != IntPtr.Zero)
                    {
                        IntPtr h = _webViewHwnd;
                        IntPtr p = GetParent(h);
                        // Walk up parents until we either reach parentHwnd or null
                        while (p != IntPtr.Zero && p != parentHwnd)
                        {
                            h = p;
                            p = GetParent(h);
                        }
                        if (p == parentHwnd) return _webViewHwnd; // still valid
                        // otherwise fall through to refresh the cached value
                        _webViewHwnd = IntPtr.Zero;
                    }
                    else
                    {
                        // No top-level to validate against; clear cache to be safe
                        _webViewHwnd = IntPtr.Zero;
                    }
                }
            }
            catch { _webViewHwnd = IntPtr.Zero; }
            
            if (_webView == null) return IntPtr.Zero;

            try
            {
                _platformWebViewFieldCache ??= _webView.GetType().GetField(
                    "_platformWebView", BindingFlags.NonPublic | BindingFlags.Instance);
                // Heuristic: if the standard field name isn't present, search for a field
                // whose type name contains 'Platform' or 'PlatformWebView' or which
                // exposes a _coreWebView2Controller property.
                if (_platformWebViewFieldCache == null)
                {
                    foreach (var f in _webView.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                    {
                        try
                        {
                            var ft = f.FieldType;
                            if (ft.Name.IndexOf("Platform", StringComparison.OrdinalIgnoreCase) >= 0 || ft.Name.IndexOf("AvaloniaWebView", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                // quick check: does this field's type have a _coreWebView2Controller property?
                                var p = ft.GetProperty("_coreWebView2Controller", BindingFlags.NonPublic | BindingFlags.Instance);
                                if (p != null)
                                {
                                    _platformWebViewFieldCache = f;
                                    break;
                                }
                            }
                        }
                        catch { }
                    }
                }
                if (_platformWebViewFieldCache == null) return IntPtr.Zero;

                var platformWebView = _platformWebViewFieldCache.GetValue(_webView);
                if (platformWebView == null) return IntPtr.Zero;

                var platformType = platformWebView.GetType();
                if (_coreControllerPropCache == null || _coreControllerPropCache.DeclaringType != platformType)
                    _coreControllerPropCache = platformType.GetProperty(
                        "_coreWebView2Controller", BindingFlags.NonPublic | BindingFlags.Instance);
                if (_coreControllerPropCache == null) return IntPtr.Zero;

                var controller = _coreControllerPropCache.GetValue(platformWebView);
                if (controller == null) return IntPtr.Zero;

                var controllerType = controller.GetType();

                // 1) Try common "ParentWindow" field first (existing behavior)
                var parentField = controllerType.GetField("ParentWindow", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (parentField != null)
                {
                    var val = parentField.GetValue(controller);
                    if (val is IntPtr hwnd && hwnd != IntPtr.Zero)
                    {
                        Console.WriteLine($"[Spotify] Discovered WebView HWND via field ParentWindow: {hwnd}");
                        if (_webViewHwnd != hwnd) _lastAppliedVisualZoom = double.NaN;
                        _webViewHwnd = hwnd;
                        return hwnd;
                    }
                }

                // 2) Try a ParentWindow property if present
                var parentProp = controllerType.GetProperty("ParentWindow", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (parentProp != null && parentProp.GetMethod != null)
                {
                    try
                    {
                        var val = parentProp.GetValue(controller);
                        if (val is IntPtr hwndProp && hwndProp != IntPtr.Zero)
                        {
                            Console.WriteLine($"[Spotify] Discovered WebView HWND via property ParentWindow: {hwndProp}");
                            if (_webViewHwnd != hwndProp) _lastAppliedVisualZoom = double.NaN;
                            _webViewHwnd = hwndProp;
                            return hwndProp;
                        }
                        // some implementations might use a long/int for handle
                        if (val is long l && l != 0)
                        {
                            var h = new IntPtr(l);
                            Console.WriteLine($"[Spotify] Discovered WebView HWND via property ParentWindow (long): {h}");
                            if (_webViewHwnd != h) _lastAppliedVisualZoom = double.NaN;
                            _webViewHwnd = h;
                            return h;
                        }
                        if (val is int i && i != 0)
                        {
                            var h = new IntPtr(i);
                            Console.WriteLine($"[Spotify] Discovered WebView HWND via property ParentWindow (int): {h}");
                            if (_webViewHwnd != h) _lastAppliedVisualZoom = double.NaN;
                            _webViewHwnd = h;
                            return h;
                        }
                    }
                    catch { /* ignore getter failures */ }
                }

                // 3) Fallback: scan other fields and properties for any IntPtr/long/int that looks like an HWND
                foreach (var f in controllerType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    try
                    {
                        var ft = f.FieldType;
                        if (ft == typeof(IntPtr) || ft == typeof(long) || ft == typeof(int))
                        {
                            var v = f.GetValue(controller);
                            IntPtr cand = IntPtr.Zero;
                            if (v is IntPtr iv && iv != IntPtr.Zero) cand = iv;
                            else if (v is long lv && lv != 0) cand = new IntPtr(lv);
                            else if (v is int iv2 && iv2 != 0) cand = new IntPtr(iv2);

                            if (cand != IntPtr.Zero)
                            {
                                Console.WriteLine($"[Spotify] Discovered WebView HWND via field {f.Name}: {cand}");
                                if (_webViewHwnd != cand) _lastAppliedVisualZoom = double.NaN;
                                _webViewHwnd = cand;
                                return cand;
                            }
                        }
                    }
                    catch { }
                }

                foreach (var p in controllerType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (p.GetMethod == null) continue;
                    try
                    {
                        var pt = p.PropertyType;
                        if (pt == typeof(IntPtr) || pt == typeof(long) || pt == typeof(int))
                        {
                            var v = p.GetValue(controller);
                            IntPtr cand = IntPtr.Zero;
                            if (v is IntPtr iv && iv != IntPtr.Zero) cand = iv;
                            else if (v is long lv && lv != 0) cand = new IntPtr(lv);
                            else if (v is int iv2 && iv2 != 0) cand = new IntPtr(iv2);

                            if (cand != IntPtr.Zero)
                            {
                                Console.WriteLine($"[Spotify] Discovered WebView HWND via property {p.Name}: {cand}");
                                if (_webViewHwnd != cand) _lastAppliedVisualZoom = double.NaN;
                                _webViewHwnd = cand;
                                return cand;
                            }
                        }
                    }
                    catch { }
                }

                Console.WriteLine("[Spotify] ParentWindow field missing or zero");
                return IntPtr.Zero;
             }
             catch (Exception ex)
             {
                 Console.WriteLine($"[Spotify] TryGetWebViewHwnd failed: {ex.Message}");
                 return IntPtr.Zero;
             }
         }

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

        [DllImport("user32.dll")]
        private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string? lpszClass, string? lpszWindow);

        [DllImport("user32.dll")]
        private static extern IntPtr GetParent(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateRoundRectRgn(int nLeftRect, int nTopRect, int nRightRect, int nBottomRect, int nWidthEllipse, int nHeightEllipse);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }
    }
}


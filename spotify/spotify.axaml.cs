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
            DisposeAncestorSubs();
            _ownRenderTransformSub?.Dispose();
            _ownRenderTransformSub = null;
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

                    try { urlProp?.SetValue(webView, new Uri(SpotifyUrl)); }
                    catch (Exception ex) { ShowError($"Failed to navigate to Spotify:\n{ex.InnerException?.Message ?? ex.Message}"); }
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

        // Live reference to the reflectively-created WebView control — used by
        // ForceWebView2Repositioning and the parent-transform subscription so the
        // WebView2 DComp surface stays glued to the Avalonia layout.
        private Control? _webView;

        private IDisposable? _ownRenderTransformSub;

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

        // Cached reflection handles for ForceWebView2Repositioning — looked up once.
        private static FieldInfo?    _platformWebViewFieldCache;
        private static PropertyInfo? _coreControllerPropCache;
        private static PropertyInfo? _controllerBoundsPropCache;
        private static PropertyInfo? _controllerIsVisiblePropCache;
        private static MethodInfo?   _notifyParentMovedMethodCache;
        private static PropertyInfo? _controllerParentWindowPropCache;

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
                if (_platformWebViewFieldCache == null) return false;

                var platformWebView = _platformWebViewFieldCache.GetValue(_webView);
                if (platformWebView == null) return false;

                var platformType = platformWebView.GetType();
                if (_coreControllerPropCache == null || _coreControllerPropCache.DeclaringType != platformType)
                {
                    _coreControllerPropCache = platformType.GetProperty(
                        "_coreWebView2Controller", BindingFlags.NonPublic | BindingFlags.Instance);
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
            int diameter = (int)(10 * scaling * 2); // CornerRadius=10 → physical pixel diameter

            try
            {
                var ourHwnd = TryGetWebViewHwnd();
                if (ourHwnd != IntPtr.Zero)
                {
                    if (GetClientRect(ourHwnd, out RECT r))
                    {
                        int w = r.Right - r.Left;
                        int h = r.Bottom - r.Top;
                        if (w > 50 && h > 50)
                        {
                            IntPtr rgn = CreateRoundRectRgn(0, 0, w + 1, h + 1, diameter, diameter);
                            SetWindowRgn(ourHwnd, rgn, true);
                        }
                    }
                }
                else
                {
                    // Fallback when HWND isn't known yet.
                    IntPtr child = FindWindowEx(parentHwnd, IntPtr.Zero, null, null);
                    while (child != IntPtr.Zero)
                    {
                        if (GetClientRect(child, out RECT r))
                        {
                            int w = r.Right - r.Left;
                            int h = r.Bottom - r.Top;
                            if (w > 50 && h > 50)
                            {
                                IntPtr rgn = CreateRoundRectRgn(0, 0, w + 1, h + 1, diameter, diameter);
                                SetWindowRgn(child, rgn, true);
                            }
                        }
                        child = FindWindowEx(parentHwnd, child, null, null);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Spotify] ApplyWebViewRoundedCorners failed: {ex.Message}");
            }
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
            if (_webView == null) return;

            var topLevel = TopLevel.GetTopLevel(_webView);
            if (topLevel == null) return;

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
            topLevel.UpdateLayout();

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

            var visualRect = new Rect(0, 0, local.Width, local.Height).TransformToAABB(transform.Value);
            var scaling    = topLevel.RenderScaling;
            int x = (int)(visualRect.X * scaling);
            int y = (int)(visualRect.Y * scaling);
            int w = Math.Max(1, (int)(visualRect.Width  * scaling));
            int h = Math.Max(1, (int)(visualRect.Height * scaling));

            // TransformToVisual walks up to (but does not include) the TopLevel's own
            // RenderTransform. The main Docklys app slides the entire MainWindow via that
            // transform, so we have to add it manually. In the editor it's an ancestor
            // StackPanel sliding instead — that transform IS included by TransformToVisual,
            // so this addition is a no-op (TopLevel.RenderTransform is null/identity there).
            if (topLevel.RenderTransform is TranslateTransform topTransform)
            {
                x += (int)(topTransform.X * scaling);
                y += (int)(topTransform.Y * scaling);
            }
            int targetX = x;

            try
            {
                var ourHwnd = TryGetWebViewHwnd();
                if (ourHwnd != IntPtr.Zero)
                {
                     // Move the outermost native child window that is still a descendant
                    // of the TopLevel. Some platform hosts wrap the real WebView HWND
                    // inside additional HWNDs which carry visual chrome (rounded
                    // frame). Moving only the innermost HWND leaves that outer frame
                    // behind, producing misalignment when sliding/scaling.
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
                    catch { /* defensive: if GetParent fails, fall back to ourHwnd */ }

                    // Position the outer window (the one whose origin is in TopLevel coords).
                    SetWindowPos(outerHwnd, IntPtr.Zero, targetX, y, w, h, SWP_NOZORDER | SWP_NOACTIVATE);

                    // If we moved a wrapper (outer != inner), ensure the inner WebView HWND
                    // fills the moved container so the DComp surface is at 0,0..w,h.
                    if (outerHwnd != ourHwnd)
                    {
                        // Size inner to 0,0 in the outer's client coords.
                        SetWindowPos(ourHwnd, IntPtr.Zero, 0, 0, w, h, SWP_NOZORDER | SWP_NOACTIVATE);
                        Console.WriteLine($"[Spotify] Positioned outerHwnd={outerHwnd} innerHwnd={ourHwnd} -> x={targetX},y={y},w={w},h={h}");
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
                            if (cw > 50 && ch > 50)
                                SetWindowPos(child, IntPtr.Zero, targetX, y, w, h,
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
            ForceWebView2RepositioningWithSize(0, w, h);
        }

        // Same as ForceWebView2Repositioning but with explicit width/height so we can
        // pass the SCALED size (accounting for ancestor ScaleTransform) instead of the
        // un-scaled local Bounds * RenderScaling that the base overload uses.
        private bool ForceWebView2RepositioningWithSize(int physicalOffsetX, int w, int h)
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
                    _coreControllerPropCache = platformType.GetProperty("_coreWebView2Controller",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                if (_coreControllerPropCache == null) return false;
                var controller = _coreControllerPropCache.GetValue(platformWebView);
                if (controller == null) return false;
                var controllerType = controller.GetType();

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
            if (_webViewHwnd != IntPtr.Zero) return _webViewHwnd;
            if (_webView == null) return IntPtr.Zero;

            try
            {
                _platformWebViewFieldCache ??= _webView.GetType().GetField(
                    "_platformWebView", BindingFlags.NonPublic | BindingFlags.Instance);
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

                _controllerParentWindowPropCache ??= controller.GetType().GetProperty("ParentWindow");
                var hwndVal = _controllerParentWindowPropCache?.GetValue(controller);
                if (hwndVal is IntPtr hwnd && hwnd != IntPtr.Zero)
                {
                    _webViewHwnd = hwnd;
                    return hwnd;
                }
            }
            catch { }

            return IntPtr.Zero;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        private const uint SWP_NOZORDER   = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string? lpszClass, string? lpszWindow);

        [DllImport("user32.dll")]
        private static extern IntPtr GetParent(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hwnd, out RECT lpRect);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int cx, int cy);

        [DllImport("user32.dll")]
        private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);
    }
}

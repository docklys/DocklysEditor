#if LINUX
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

namespace RunModule;

/// <summary>
/// Makes WebKit native children respect the editor's module zoom and carousel transforms.
/// NativeControlHost lays them out at their untransformed size; on Xwayland that can cover the
/// rest of a module or be painted off its visible bounds. Dockly already owns this correction for
/// the live dock, while the standalone editor needs its own small, host-local equivalent.
/// </summary>
internal static class X11WebViewLayoutSync
{
    private static readonly List<WeakReference<Window>> Hosts = new();
    private static DispatcherTimer? _timer;
    private static nint _display;
    private static XErrorHandler? _errorHandler;

    internal static void Start(Window host)
    {
        if (Hosts.Exists(reference => reference.TryGetTarget(out var current) && ReferenceEquals(current, host)))
            return;

        Hosts.Add(new WeakReference<Window>(host));
        _timer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Background,
            (_, _) => Tick());
        _timer.Start();
    }

    private static void Tick()
    {
        if (_display == IntPtr.Zero)
        {
            _display = XOpenDisplay(IntPtr.Zero);
            if (_display == IntPtr.Zero) return;
            // A module can be removed while a queued frame is synchronizing. Xlib's default
            // BadWindow handler terminates the whole process, which is never acceptable for an
            // editor preview.
            _errorHandler = (_, _) => 0;
            XSetErrorHandler(Marshal.GetFunctionPointerForDelegate(_errorHandler));
        }

        for (var i = Hosts.Count - 1; i >= 0; i--)
        {
            if (!Hosts[i].TryGetTarget(out var host) || !host.IsVisible)
            {
                Hosts.RemoveAt(i);
                continue;
            }

            try { SyncHost(host); }
            catch { /* A destroyed native child is retried/removed next frame. */ }
        }

        if (Hosts.Count == 0) _timer?.Stop();
    }

    private static void SyncHost(Window host)
    {
        var topHandle = host.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (topHandle == IntPtr.Zero) return;

        foreach (var control in host.GetVisualDescendants().OfType<Control>())
        {
            if (!string.Equals(control.GetType().FullName, "AvaloniaWebView.WebView", StringComparison.Ordinal))
                continue;
            if (!control.IsEffectivelyVisible || control.Bounds.Width <= 0 || control.Bounds.Height <= 0)
                continue;

            var native = ResolveNativeWindow(control);
            if (native == IntPtr.Zero) continue;
            var holder = QueryParent(native);
            if (holder == IntPtr.Zero || QueryParent(holder) != topHandle) continue;

            var matrix = control.TransformToVisual(host);
            if (!matrix.HasValue) continue;
            var composed = matrix.Value;
            if (host.RenderTransform != null) composed *= host.RenderTransform.Value;

            var visual = new Rect(default, control.Bounds.Size).TransformToAABB(composed);
            var scaling = host.RenderScaling;
            var scale = VisualScale(composed);
            // Match Dockly's content gutter: it keeps WebKit from hiding the module's border
            // or the bottom settings affordance when the editor scales a preview.
            var inset = Math.Max(1, (int)Math.Round(6 * scale * scaling));
            // Full (unclipped) module rect in physical pixels.
            var moduleX = (int)Math.Round(visual.X * scaling) + inset;
            var moduleY = (int)Math.Round(visual.Y * scaling) + inset;
            var moduleW = Math.Max(1, (int)Math.Round(visual.Width * scaling) - inset * 2);
            var moduleH = Math.Max(1, (int)Math.Round(visual.Height * scaling) - inset * 2);

            // Clip the native child to the same bounds Avalonia clips the module to. A WebKit X11
            // child ignores ancestor ClipToBounds, so without this it spills past the LIVE PREVIEW
            // border and paints over the rest of the editor — most visibly when the zoom slider
            // enlarges the tile past the preview viewport, or when the preview panel is small.
            var clip = ComputeClipRect(control, host, scaling);
            var ix = Math.Max(moduleX, clip.X);
            var iy = Math.Max(moduleY, clip.Y);
            var iRight = Math.Min(moduleX + moduleW, clip.X + clip.W);
            var iBottom = Math.Min(moduleY + moduleH, clip.Y + clip.H);
            var vw = iRight - ix;
            var vh = iBottom - iy;
            if (vw <= 0 || vh <= 0)
            {
                // Entirely outside the visible preview — park it off-screen so it covers nothing.
                XMoveResizeWindow(_display, holder, -32000, -32000, 1, 1);
                continue;
            }

            // Holder = the visible (clipped) region; the native child keeps the full module size,
            // shifted so only the in-bounds slice shows (the holder window clips its own child).
            var offX = moduleX - ix;
            var offY = moduleY - iy;
            XMoveResizeWindow(_display, holder, ix, iy, (uint)vw, (uint)vh);
            XMoveResizeWindow(_display, native, offX, offY, (uint)moduleW, (uint)moduleH);

            // A native child is a square X window and knows nothing about the module's corner
            // radius, so WebKit paints square corners over it. Dockly rounds the holder via the
            // X Shape extension; do the same here or previews misrepresent the shipped module.
            // The shape is built for the full module and offset into the (possibly clipped)
            // holder, so only the module's real corners round and clipped edges stay straight.
            // Re-shaping costs a server round-trip, so only do it when the geometry changed.
            var radius = Math.Max(1, (int)Math.Round(WebViewCornerRadiusLogical * scale * scaling));
            var shapeKey = (vw, vh, radius, offX, offY, moduleW, moduleH);
            if (!ShapeCache.TryGetValue(holder, out var last) || last != shapeKey)
            {
                ApplyRoundedBoundingShape(holder, moduleW, moduleH, radius, offX, offY);
                ShapeCache[holder] = shapeKey;
            }
        }
        XFlush(_display);
    }

    // Matches Dockly's WebViewCornerRadiusLogical: deliberately smaller than the module's own
    // outer radius so the two contours don't fight along the same edge.
    private const double WebViewCornerRadiusLogical = 6.0;

    // Last shape written per holder window, so a static preview costs nothing per frame.
    private static readonly Dictionary<nint, (int W, int H, int R, int OffX, int OffY, int MW, int MH)> ShapeCache = new();

    // Intersects the bounds of every ClipToBounds ancestor (the LIVE PREVIEW border and any
    // other clipping container) between the webview and the window, in physical pixels. This is
    // the region Avalonia would clip the module to; the native X11 child must be clipped the
    // same way or it renders over the rest of the editor.
    private static (int X, int Y, int W, int H) ComputeClipRect(Control webView, Window host, double scaling)
    {
        double left = 0, top = 0, right = host.Bounds.Width, bottom = host.Bounds.Height;

        Visual? v = webView.GetVisualParent();
        while (v != null && !ReferenceEquals(v, host))
        {
            if (v.ClipToBounds)
            {
                var m = v.TransformToVisual(host);
                if (m.HasValue)
                {
                    var composed = m.Value;
                    if (host.RenderTransform != null) composed *= host.RenderTransform.Value;
                    var r = new Rect(default, v.Bounds.Size).TransformToAABB(composed);
                    if (r.X > left) left = r.X;
                    if (r.Y > top) top = r.Y;
                    if (r.X + r.Width < right) right = r.X + r.Width;
                    if (r.Y + r.Height < bottom) bottom = r.Y + r.Height;
                }
            }
            v = v.GetVisualParent();
        }

        var x = (int)Math.Round(left * scaling);
        var y = (int)Math.Round(top * scaling);
        return (x, y, Math.Max(0, (int)Math.Round(right * scaling) - x), Math.Max(0, (int)Math.Round(bottom * scaling) - y));
    }

    // Approximates a rounded rectangle with one-pixel rows down each corner arc plus a single
    // rectangle for the straight middle — the same construction Dockly uses. Built at the full
    // module size and shifted by (offX, offY) into the holder so a clipped tile keeps rounded
    // corners only where its real corners fall inside the visible region.
    private static void ApplyRoundedBoundingShape(nint window, int w, int h, int r, int offX, int offY)
    {
        r = Math.Min(r, Math.Min(w, h) / 2);
        if (r < 1) return;

        var rects = new List<XRectangle>(2 * r + 1);
        for (var row = 0; row < r; row++)
        {
            var inset = r - (int)Math.Round(Math.Sqrt((double)r * r - (r - row - 0.5) * (r - row - 0.5)));
            rects.Add(new XRectangle { X = (short)(offX + inset), Y = (short)(offY + row), Width = (ushort)Math.Max(1, w - 2 * inset), Height = 1 });
            rects.Add(new XRectangle { X = (short)(offX + inset), Y = (short)(offY + h - 1 - row), Width = (ushort)Math.Max(1, w - 2 * inset), Height = 1 });
        }
        rects.Add(new XRectangle { X = (short)offX, Y = (short)(offY + r), Width = (ushort)w, Height = (ushort)Math.Max(1, h - 2 * r) });

        XShapeCombineRectangles(_display, window, ShapeBounding, 0, 0, rects.ToArray(), rects.Count, ShapeSet, 0);
    }

    private static nint ResolveNativeWindow(Control webView)
    {
        var platform = webView.GetType().GetField("_platformWebView",
                BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(webView);
        var handler = platform?.GetType().GetProperty("NativeHandler")?.GetValue(platform);
        return handler is nint handle ? handle : IntPtr.Zero;
    }

    private static nint QueryParent(nint window)
    {
        if (XQueryTree(_display, window, out _, out var parent, out var children, out _) == 0)
            return IntPtr.Zero;
        if (children != IntPtr.Zero) XFree(children);
        return parent;
    }

    private static double VisualScale(Matrix matrix)
    {
        var origin = matrix.Transform(default);
        var x = matrix.Transform(new Point(1, 0));
        var y = matrix.Transform(new Point(0, 1));
        var sx = Math.Sqrt(Math.Pow(x.X - origin.X, 2) + Math.Pow(x.Y - origin.Y, 2));
        var sy = Math.Sqrt(Math.Pow(y.X - origin.X, 2) + Math.Pow(y.Y - origin.Y, 2));
        return double.IsFinite(sx) && double.IsFinite(sy) && sx > 0 && sy > 0 ? (sx + sy) / 2 : 1;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XErrorHandler(nint display, nint error);

    [StructLayout(LayoutKind.Sequential)]
    private struct XRectangle { public short X, Y; public ushort Width, Height; }

    private const int ShapeBounding = 0;
    private const int ShapeSet = 0;

    [DllImport("libX11.so.6")] private static extern nint XOpenDisplay(nint display);
    [DllImport("libX11.so.6")] private static extern nint XSetErrorHandler(nint handler);
    [DllImport("libX11.so.6")] private static extern int XQueryTree(nint display, nint window, out nint root, out nint parent, out nint children, out uint childCount);
    [DllImport("libX11.so.6")] private static extern int XFree(nint data);
    [DllImport("libX11.so.6")] private static extern int XMoveResizeWindow(nint display, nint window, int x, int y, uint width, uint height);
    [DllImport("libX11.so.6")] private static extern int XFlush(nint display);
    [DllImport("libXext.so.6")] private static extern void XShapeCombineRectangles(nint display, nint window, int destKind, int xOff, int yOff, XRectangle[] rectangles, int nRects, int op, int ordering);
}
#endif

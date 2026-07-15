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
            var x = (int)Math.Round(visual.X * scaling) + inset;
            var y = (int)Math.Round(visual.Y * scaling) + inset;
            var width = Math.Max(1, (int)Math.Round(visual.Width * scaling) - inset * 2);
            var height = Math.Max(1, (int)Math.Round(visual.Height * scaling) - inset * 2);

            XMoveResizeWindow(_display, holder, x, y, (uint)width, (uint)height);
            XMoveResizeWindow(_display, native, 0, 0, (uint)width, (uint)height);
        }
        XFlush(_display);
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

    [DllImport("libX11.so.6")] private static extern nint XOpenDisplay(nint display);
    [DllImport("libX11.so.6")] private static extern nint XSetErrorHandler(nint handler);
    [DllImport("libX11.so.6")] private static extern int XQueryTree(nint display, nint window, out nint root, out nint parent, out nint children, out uint childCount);
    [DllImport("libX11.so.6")] private static extern int XFree(nint data);
    [DllImport("libX11.so.6")] private static extern int XMoveResizeWindow(nint display, nint window, int x, int y, uint width, uint height);
    [DllImport("libX11.so.6")] private static extern int XFlush(nint display);
}
#endif

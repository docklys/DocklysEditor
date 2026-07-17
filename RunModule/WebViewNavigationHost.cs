#if LINUX
using Avalonia.Controls;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace RunModule;

/// <summary>
/// Process-wide guard for every module webview the editor previews. Dockly owns this for the
/// live dock (see its WebViewHardeningService, which this mirrors); the standalone editor needs
/// its own host-local equivalent, or the same module that behaves in the dock crashes or renders
/// blank here — which is not a useful preview.
///
/// It covers three separate WebView.Avalonia/WebKitGTK problems:
///
///   1. Navigation escapes to the system browser. The backend funnels every MAIN-FRAME
///      navigation through NewWindowRequested preset to OpenExternally; left alone the library
///      tells WebKit to IGNORE the load and launches the system browser, so the embedded view
///      never paints.
///
///   2. GtkSharp lifetime crash. Each navigation the backend creates GtkSharp wrappers around
///      WebKit-owned transients; when those are GC'd, the queued toggle-ref removal touches a
///      GObject WebKit already destroyed — SIGSEGV in g_object_remove_toggle_ref. The backend
///      also connects WebKit's decide-policy signal twice, the second an empty managed handler
///      that materializes another wrapper per navigation. Pin the wrappers and detach the
///      redundant handler.
///
///   3. WebKitGTK's stock "Safari on Linux" user agent makes major web apps bail out silently —
///      Spotify's player loads its shell and then renders nothing, i.e. a black preview.
/// </summary>
internal static class WebViewNavigationHost
{
    // The Android Chrome UA is the one these apps actually render, and its mobile layout suits
    // phone-sized module tiles. Matches Dockly's ModuleMobileUserAgent exactly, so a preview
    // reflects what the dock will show.
    private const string ModuleMobileUserAgent =
        "Mozilla/5.0 (Linux; Android 13; Pixel 7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/116.0.0.0 Mobile Safari/537.36";

    private static bool _installed;
    private static readonly ConditionalWeakTable<object, object> Hooked = new();
    private static readonly ConditionalWeakTable<object, object> PlatformHardened = new();
    private static readonly object Marker = new();

    // GtkSharp wrappers for transient WebKit objects must never reach their finalizers (see 2
    // above). Pinning is a deliberate bounded leak: one small object per navigation.
    private static readonly List<object> GtkWrapperPins = new();

    /// <summary>
    /// Installs a global Loaded class handler so every webview a module constructs is covered,
    /// no matter when or where it is created. Call once at startup.
    /// </summary>
    internal static void Install()
    {
        if (_installed) return;
        _installed = true;
        Control.LoadedEvent.AddClassHandler(
            typeof(AvaloniaWebView.WebView),
            (sender, _) =>
            {
                if (sender is AvaloniaWebView.WebView webView) Hook(webView);
            });
    }

    private static void Hook(AvaloniaWebView.WebView webView)
    {
        if (Hooked.TryGetValue(webView, out _)) return;
        Hooked.Add(webView, Marker);

        webView.NavigationStarting += (_, e) =>
        {
            NavDbg($"[HOST] NavigationStarting url='{e.Url}'");
            PinGtkWrapper(e.RawArgs);
            // GtkSharp caches wrappers by native handle, so reading Request here returns the
            // same instance the backend already made; retaining it keeps its finalizer from
            // racing WebKit teardown too.
            try
            {
                PinGtkWrapper(e.RawArgs?.GetType()
                    .GetProperty("Request", BindingFlags.Public | BindingFlags.Instance)
                    ?.GetValue(e.RawArgs));
            }
            catch { /* wrapper shape differs; pinning is best-effort */ }

            // Raised from the direct native policy callback — on the GTK thread, before Gtk
            // dispatches the library's redundant managed handler, and still ahead of the first
            // request going out. The only safe point to touch GtkSharp objects or set the UA.
            HardenPlatformWebView(webView);
        };

        webView.NavigationCompleted += (_, e) =>
        {
            NavDbg($"[HOST] NavigationCompleted {DumpArgs(e)}");
            PinGtkWrapper(e.RawArgs);
        };

        webView.WebViewNewWindowRequested += (_, e) =>
        {
            NavDbg($"[HOST] NewWindowRequested url='{e.Url}' strategy={e.UrlLoadingStrategy}");
            PinGtkWrapper(e.RawArgs);

            if (e.UrlLoadingStrategy == WebViewCore.Enums.UrlRequestStrategy.OpenExternally)
            {
                e.UrlLoadingStrategy = WebViewCore.Enums.UrlRequestStrategy.OpenInWebView;
                NavDbg("[HOST]   -> flipped OpenExternally to OpenInWebView");
                return;
            }

            // WebKitGTK reports target=_blank/window.open as OpenInWebView, but the backend only
            // accepts the policy decision — it never supplies WebKit's create signal with another
            // view, so the popup is never created and the link silently does nothing. Cancel the
            // orphaned popup and drive this view to the URL instead.
            if (e.UrlLoadingStrategy == WebViewCore.Enums.UrlRequestStrategy.OpenInWebView
                && IsNavigableWebUri(e.Url))
            {
                var popupUrl = e.Url!;
                NavDbg($"[HOST]   -> OpenInWebView popup: CancelLoad + re-post webView.Url='{popupUrl}' (current='{webView.Url}')");
                e.UrlLoadingStrategy = WebViewCore.Enums.UrlRequestStrategy.CancelLoad;
                Dispatcher.UIThread.Post(() => webView.Url = popupUrl);
            }
        };
    }

    private static void NavDbg(string msg)
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(appData, "Docklys", "webview-nav-debug.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {msg}\n");
        }
        catch { }
    }

    private static string DumpArgs(object e)
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            foreach (var p in e.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.Name == "RawArgs") continue;
                object? v = null;
                try { v = p.GetValue(e); } catch { }
                sb.Append(p.Name).Append('=').Append(v).Append(' ');
            }
            return sb.ToString().TrimEnd();
        }
        catch { return "?"; }
    }

    private static void PinGtkWrapper(object? wrapper)
    {
        if (wrapper == null) return;
        lock (GtkWrapperPins) GtkWrapperPins.Add(wrapper);
    }

    // One-time, GTK-thread-only pass over the library's platform webview.
    private static void HardenPlatformWebView(AvaloniaWebView.WebView webView)
    {
        try
        {
            var platformWebView = webView.GetType().GetField(
                "_platformWebView", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(webView);
            if (platformWebView == null) return;

            lock (PlatformHardened)
            {
                if (PlatformHardened.TryGetValue(platformWebView, out _)) return;
                PlatformHardened.Add(platformWebView, Marker);
            }

            var nativeWebView = platformWebView.GetType().GetField(
                "_webView", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(platformWebView);
            if (nativeWebView == null) return;

            try
            {
                var settings = nativeWebView.GetType().GetProperty("Settings")?.GetValue(nativeWebView);
                var settingsType = settings?.GetType();
                settingsType?.GetProperty("UserAgent")?.SetValue(settings, ModuleMobileUserAgent);
                settingsType?.GetProperty("EnableJavascript")?.SetValue(settings, true);
                settingsType?.GetProperty("EnableHtml5LocalStorage")?.SetValue(settings, true);
                Console.WriteLine("[RunModule] applied module mobile user agent");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RunModule] could not apply module user agent: {ex.Message}");
            }

            var decidePolicyEvent = nativeWebView.GetType().GetEvent("DecidePolicy");
            var handlerMethod = platformWebView.GetType()
                .GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "WebView_DecidePolicy" && m.GetParameters().Length == 2);
            if (decidePolicyEvent?.EventHandlerType == null || handlerMethod == null) return;

            decidePolicyEvent.RemoveEventHandler(nativeWebView, Delegate.CreateDelegate(
                decidePolicyEvent.EventHandlerType, platformWebView, handlerMethod));
            Console.WriteLine("[RunModule] detached redundant GtkSharp DecidePolicy handler");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RunModule] could not harden GtkSharp platform webview: {ex.Message}");
        }
    }

    private static bool IsNavigableWebUri(Uri? uri)
        => uri != null
           && (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
               || string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase));
}
#endif

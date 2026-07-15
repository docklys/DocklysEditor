#if LINUX
using Avalonia.Controls;
using Avalonia.Threading;
using System;
using System.Runtime.CompilerServices;

namespace RunModule;

/// <summary>
/// Keeps module page loads inside the editor's preview webview instead of the system browser.
/// Dockly owns this for the live dock (see its WebViewHardeningService); the standalone editor
/// needs its own small, host-local equivalent, or a module that renders correctly in the dock
/// previews here as an empty rectangle plus a stray browser tab.
/// </summary>
internal static class WebViewNavigationHost
{
    private static bool _installed;
    private static readonly ConditionalWeakTable<object, object> Hooked = new();
    private static readonly object Marker = new();

    /// <summary>
    /// Installs a global Loaded class handler, so every webview a module constructs is covered
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
        webView.WebViewNewWindowRequested += (_, e) =>
        {
            // The WebKitGTK backend funnels every MAIN-FRAME navigation through this event
            // preset to OpenExternally. Left alone, the library tells WebKit to IGNORE the load
            // and launches the system browser instead, so the embedded view never paints — this
            // is why editor previews came up blank while opening the page in a browser.
            if (e.UrlLoadingStrategy == WebViewCore.Enums.UrlRequestStrategy.OpenExternally)
            {
                e.UrlLoadingStrategy = WebViewCore.Enums.UrlRequestStrategy.OpenInWebView;
                return;
            }

            // WebKitGTK reports target=_blank/window.open navigations as OpenInWebView, but the
            // backend only accepts the policy decision — it never supplies WebKit's create signal
            // with another view, so the popup is never created and the link silently does nothing.
            // Cancel that orphaned popup and drive this view to the URL instead.
            if (e.UrlLoadingStrategy == WebViewCore.Enums.UrlRequestStrategy.OpenInWebView
                && IsNavigableWebUri(e.Url))
            {
                var popupUrl = e.Url!;
                e.UrlLoadingStrategy = WebViewCore.Enums.UrlRequestStrategy.CancelLoad;
                Dispatcher.UIThread.Post(() => webView.Url = popupUrl);
            }
        };
    }

    private static bool IsNavigableWebUri(Uri? uri)
        => uri != null
           && (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
               || string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase));
}
#endif

#if WINDOWS
// WinWebViewHost.cs - placeholder stub
// Embedded WebView2 support was temporarily disabled because the WebView2 NuGet/runtime wasn't available in this environment.
// To re-enable embedded WebView2 on Windows, re-add Microsoft.Web.WebView2 and implement platform-specific host here.

namespace VChat
{
    public static class WinWebViewHost
    {
        public static void ShowWebView(System.Uri uri)
        {
            // No-op stub: embedded WebView not available in this build.
            // The VChat module will fall back to opening the URL in the system browser.
        }
    }
}
#endif

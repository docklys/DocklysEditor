using System;
using System.Collections.Generic;

namespace Docklys.ModuleContracts;

// Keep this contract source in lockstep with Dockly/Docklys.ModuleContracts.
// Modules describe web content; the host owns the native browser integration.
public interface IWebViewModule
{
    WebViewModuleDefinition WebView { get; }
}

public sealed class WebViewModuleDefinition
{
    public string StartUrl { get; init; } = string.Empty;
    public IReadOnlyList<string> AllowedHosts { get; init; } = Array.Empty<string>();
    public bool AllowExternalNavigation { get; init; } = true;
    public WebViewUserAgentProfile UserAgentProfile { get; init; } = WebViewUserAgentProfile.Default;
    public WebViewLinuxEngine LinuxEngine { get; init; } = WebViewLinuxEngine.WebKit;
    public double InitialZoom { get; init; } = 1.0;
    public WebViewFeatures Features { get; init; } = WebViewFeatures.None;
    public bool UsePersistentCookies { get; init; }
    public bool UseChromiumLoginHandoff { get; init; }
    public IReadOnlyList<WebViewDocumentScript> DocumentScripts { get; init; } = Array.Empty<WebViewDocumentScript>();
}

public sealed record WebViewDocumentScript(string Name, string Source);

public enum WebViewUserAgentProfile
{
    Default,
    MobileChromium,
    Spotify
}

public enum WebViewLinuxEngine
{
    WebKit,
    ChromiumOverlay
}

[Flags]
public enum WebViewFeatures
{
    None = 0,
    HideScrollbars = 1 << 0,
    MiddleMouseDragGuard = 1 << 1,
    ScaleToFit = 1 << 2,
    MobileViewport = 1 << 3,
    MouseToTouch = 1 << 4
}

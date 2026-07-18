# Building Modules for Docklys — Documentation

Welcome. This guide walks you through everything you need to build a module for Docklys, from "I have an empty folder" to "my module is live in the dock with the right theme, saving its state across restarts."

> **Mental model.** A *module* is a small Avalonia `UserControl` you compile to a single `.dll`. Docklys's main window scans a folder of these DLLs, instantiates the ones it finds, and arranges them on a tiled grid. Your module is a small island of UI that runs inside the host's window.

For a terse, machine-grade reference, see [`AIArchitecture.md`](./AIArchitecture.md). For Editor specifics, see [`README.md`](./README.md).

## Contents

1. [The 30-Second Tour](#1-the-30-second-tour)
2. [Creating a New Module](#2-creating-a-new-module)
3. [The `IModule` Contract & MCCP](#3-the-imodule-contract--mccp)
4. [Discovery & Module Reload](#4-discovery--module-reload)
5. [App Lifecycle](#5-app-lifecycle)
6. [Tabs (Profiles) & Re-rendering](#6-tabs-profiles--re-rendering)
7. [Saving State to `%APPDATA%`](#7-saving-state-to-appdata)
8. [The Theme System (Skins)](#8-the-theme-system-skins)
9. [Linux Desktop Support](#9-linux-desktop-support)
10. [Dockly.Browser (WebAssembly)](#10-docklybrowser-webassembly)
11. [Browser and WebView Modules](#11-browser-and-webview-modules)
12. [Common Pitfalls](#12-common-pitfalls)
13. [AI and cross-platform development](#13-ai-and-cross-platform-development)

---

## 1. The 30-Second Tour

```text
DocklysEditor/
├── DefaultModule/              ← copy-this-to-start template
├── VolumeMixer/                ← reference module, real-world example
├── Docklys.ModuleContracts/    ← IModule interface + SkinKeys constants
└── RunModule/                  ← the editor: preview your module live

Dockly/Dockly/
├── Skins/Default.axaml         ← the skins your module renders under
├── Modules/ModuleRegistry.cs   ← the loader that finds your DLL
├── Modules/              ← drop your built DLL here
└── Views/MainWindow.*.cs       ← the tile grid + profile (tab) system
```

---

## 2. Creating a New Module

You have three options to create a module:

### Option A — "New module" Button in RunModule (Easiest)
1. Open `DefaultModule.sln` and run the `RunModule` project.
2. Click **New module** in the top bar.
3. Enter a name (e.g. `Clock`).
4. Let the editor create and build the project; the module then appears in the carousel.

### Option B — `dotnet new docklysmodule`
From the repo root:
```sh
dotnet new --install ./DocklysEditor/DefaultModule
dotnet new docklysmodule -n Clock -o ./DocklysEditor/Clock
```

### Option C — Manual Copy
Copy the `DefaultModule` folder manually, rename everything from `DefaultModule` to your module name, add it to `DefaultModule.sln`, and add a `<ProjectReference>` in `RunModule.csproj`.

### Module manifest and permissions

Every module folder must include `docklys.manifest.json`. This is the security declaration for the compiled module; keep its `module_id` aligned with the module folder/assembly and keep its version aligned with the module version you ship.

```json
{
  "schema_version": 1,
  "module_id": "Clock",
  "version": "1.0.0",
  "security_tier": 1,
  "requested_capabilities": [
    "ui.render",
    "storage.module.read",
    "storage.module.write"
  ]
}
```

- `ui.render` is required by every visible module.
- `storage.module.read` and `storage.module.write` permit only the module's own settings data. Request both and use `security_tier` 1 when the module persists state.
- Do not request storage or any future capability “just in case.” Start with the least privilege the module needs and add a capability only when the implementation requires it.
- Use **Project → Permissions** in `RunModule` to edit the manifest version, standard storage permissions, and additional capability names. The editor calculates `security_tier` from the selected capabilities; developers do not set it manually. The New Module dialog preselects storage access so a settings-capable template is safe by default; remove it for a truly stateless module.

The editor copies the active module's manifest into `OutputModuleDLL` with its package metadata. A deployment format that stores modules separately must keep each manifest with its corresponding module, never substitute another module's manifest.

---

## 3. The `IModule` Contract & MCCP

Your `UserControl` must implement `Docklys.ModuleContracts.IModule` as part of the **MCCP (Module Communication & Control Protocol)**.

```csharp
public interface IModule {
    string ModuleName    { get; }
    string ModuleVersion { get; }
    string UniqueModuleId { get; }
    void SetModuleId(string uniqueModuleId);
    void PrintModuleId();
    int PreferredTileWidth  => 1;
    int PreferredTileHeight => 1;
}
```

A minimal implementation:
```csharp
using Avalonia.Controls;
using Docklys.ModuleContracts;

namespace Clock
{
    public partial class Clock : UserControl, IModule
    {
        public string ModuleName    => "Clock";
        public string ModuleVersion => "1.0.0";

        private string? _uniqueModuleId;
        public string UniqueModuleId => _uniqueModuleId ?? "";

        public void SetModuleId(string uniqueModuleId) => _uniqueModuleId = uniqueModuleId;
        public void PrintModuleId() => System.Console.WriteLine($"Module ID: {UniqueModuleId}");

        public int PreferredTileWidth  => 2;
        public int PreferredTileHeight => 2;

        public Clock() => InitializeComponent();
    }
}
```

- **`UniqueModuleId` is set by the Host.** Store it via `SetModuleId(string)`. MUST be used for state persistence keys.
- **`PreferredTileWidth` / `Height` are hints.** Design your XAML to scale.

### Other Core MCCP Interfaces
- **`IResizable`**: Opt-in for runtime tile resizing. Use `TileResizeRequested` to notify the host, and implement `SetTileSize(w, h)` to react.
- **`IInteractionFreezable`**: Implement this if you have native overlays (like WebView2). The host calls `FreezeInteraction()` when dragging or showing settings to prevent the native window from capturing input.
- **`IWebViewModule`**: Declares browser content and policy while Docklys owns the native browser, geometry, cookies, and platform workarounds. See [Browser and WebView Modules](#11-browser-and-webview-modules).

---

## 4. Discovery & Module Reload

When Dockly starts, `ModuleRegistry` scans for built-in modules and external DLLs.

**Where to put your `.dll`:**
1. `$DOCKLY_MODULES_PATH` (env var)
2. `%APPDATA%/Docklys/Modules/`
3. Next to the executable in `Modules/`
4. A source-tree `Modules/` directory found by walking upward
5. The platform local-app-data `Docklys/Modules/` fallback

Distributed builds require a valid module signature and block legacy user-managed in-process directories. Developer loading can be enabled with the explicit environment variables or marker files described in `ModuleRegistry.cs`; do not ask end users to disable these checks.

**Module Reloading:**
There is no filesystem watcher in the main app. Dropping a DLL requires restarting Docklys. However, the Editor (`RunModule`) handles **Hot-Reload** via a custom `AssemblyLoadContext`. When you rebuild `RunModule`, it re-loads the DLLs into memory.

---

## 5. App Lifecycle

1. **Initialization:** Avalonia loads, finds the skin, and installs it into `Application.Current.Styles`. **Your module's XAML is parsed AFTER the skin is installed**, so `{DynamicResource ColorAccent}` works immediately.
2. **First Render:** `MainWindow` reads `%APPDATA%/Docklys/tiles.json`, generates the empty grid, and places your module.
3. **Lazy Constructor:** Your `ctor` only runs when the host needs to place your module instance on the grid.

---

## 6. Tabs (Profiles) & Re-rendering

Docklys's bottom buttons (Profile 1...5) are tabs. When switching:
1. Host saves outgoing profile.
2. Caches live instances in memory.
3. Clears visual tree.
4. Restores incoming profile. **Your ctor does NOT run again** if cached. Avalonia's `Unloaded`/`Loaded` events fire instead.

**A Safe Pattern:**
```csharp
public Clock() {
    InitializeComponent();
    this.Loaded   += OnLoaded;
    this.Unloaded += OnUnloaded;
}

private void OnLoaded(object? sender, RoutedEventArgs e) {
    if (!_stateLoaded) { LoadState(); _stateLoaded = true; }
    StartTimer();
}

private void OnUnloaded(object? sender, RoutedEventArgs e) {
    StopTimer();
    // Do NOT auto-save here to prevent disk thrashing. Save on user action!
}
```

---

## 7. Saving State to `%APPDATA%`

Docklys does **not** provide a global settings API for modules. Modules are responsible for persisting their own state. However, to keep the user's filesystem clean, you are **only** allowed to save internal custom settings to a specific directory convention.

### The Required Path Convention
```csharp
private static readonly string SettingsPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "Docklys", "ModuleSaves", "Clock", $"{UniqueModuleId}.json");
```

### Persistence Best Practices
1. **Always Use `UniqueModuleId`:** Your filename **must** incorporate the `UniqueModuleId` assigned to your instance by the host. A user can place multiple "Clock" modules on the same grid; using just "settings.json" will cause the instances to overwrite each other.
2. **Create Directories Before Writing:** The host doesn't create your module's save folder for you. Use `Directory.CreateDirectory` before your `File.WriteAllText` call.
3. **Save on User Action, Load on `Loaded`:** 
   - **Load:** Load your state when the `Loaded` event fires, as this guarantees your module is fully attached to the visual tree. 
   - **Save:** Only save when the user changes a setting (e.g., toggling a switch or ending a slider drag). **Never auto-save in the `Unloaded` event**. Tab switches detach and re-attach modules rapidly; saving on `Unloaded` will thrash the user's disk.
4. **Use JSON:** Stick to `System.Text.Json` for simple, human-readable configurations.

```csharp
private void SaveState() {
    Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
    var json = JsonSerializer.Serialize(new ClockState { ShowSeconds = true });
    File.WriteAllText(SettingsPath, json);
}
```

---

## 8. The Theme System (Skins)

> **Rule:** Do not ship inline styles for primitives the host already themes. Use the active skin.

The host loads one skin from `Dockly/Skins/` at startup.

**Doing it Right:**
Reference `SkinKeys` keys via `{DynamicResource}`:
```xaml
<Border Background="{DynamicResource ColorModuleColor}" CornerRadius="10" Padding="6">
    <TextBlock Text="12:34" Foreground="{DynamicResource ColorModuleFont}" />
    <Slider Theme="{DynamicResource ModuleSliderTheme}" />
</Border>
```

**Doing it Wrong:**
- Hardcoding colors (`Background="#363737"`).
- Using StaticResource (`{StaticResource ColorModuleColor}`).
- Redefining themes the skin owns (`<ControlTheme x:Key="ModuleSliderTheme">`).

---

## 9. Linux Desktop Support

Docklys and `RunModule` compile Linux-specific code behind the `LINUX` constant. The desktop host targets .NET 10 and uses Avalonia's X11/XWayland path for native browser embedding.

### Runtime paths and module behavior

- `Environment.SpecialFolder.ApplicationData` resolves to the user's XDG configuration location, normally `~/.config`. Module state therefore lives under `~/.config/Docklys/ModuleSaves/`.
- Linux module DLLs still use the same `IModule`, `IResizable`, theme, and persistence contracts as Windows modules.
- A module may expose a `SupportedPlatforms` metadata property, but it must still degrade safely when constructed elsewhere. `RunModule` intentionally previews constructible modules even when their metadata does not list the developer's OS, so layout and skin work remains possible.
- Platform-specific services must be guarded with `OperatingSystem.IsLinux()`, `OperatingSystem.IsWindows()`, or compile-time constants. Do not execute Windows APIs from a constructor merely because the UI can be previewed on Linux.

### Linux native-browser requirements

Desktop webviews use WebKitGTK through `WebView.Avalonia`. Docklys probes for `libwebkit2gtk-4.0.so.37` before constructing the marketplace webview because constructing it without the library can block the UI thread. If the dependency is absent, the settings panel offers a package-manager install command and an **Open marketplace in browser** fallback.

At process startup, Docklys and `RunModule` set two variables in the native Unix environment before GTK loads:

- `GDK_BACKEND=x11` ensures WebKitGTK exposes an XID that Avalonia can reparent. On a Wayland desktop this means the embedded surface runs through XWayland.
- `WEBKIT_DISABLE_DMABUF_RENDERER=1` avoids WebKitGTK DMA-BUF flicker when the view is reparented under XWayland. A user-provided value is preserved.

Native WebKit children do not automatically follow Avalonia `RenderTransform` values. `X11WebViewOverlaySync` in Docklys and `X11WebViewLayoutSync` in `RunModule` periodically apply transform-aware X11 geometry, content insets, and rounded X Shape regions. `NativeWebViewAnimationSync` performs the same correction on slide-animation frames.

### Other Linux desktop integration

The Linux desktop entry is registered for portable/IDE launches, and a global named mutex keeps one Docklys instance alive. A second launch sends a `show-main.cmd` command through `~/.config/Docklys/commands/` and exits. These are host responsibilities; modules must not create their own instance or compositor management.

---

## 10. Dockly.Browser (WebAssembly)

`Dockly.Browser` is the browser/WASM frontend. It targets `net10.0-browser`, references the shared `Dockly` project, and starts Avalonia with `StartBrowserAppAsync("out")`. The shared project adds the `BROWSER` compile constant for this target.

The browser build is not a desktop window running inside a webview:

- There is no operating-system window handle, screen collection, global keyboard/mouse hook, tray, native child window, X11 API, or WebView2/WebKitGTK instance.
- `MainWindow` supplies browser-safe window-like shims for shared layout code.
- `WebViewStub.cs` provides a no-op `AvaloniaWebView.WebView` shape so shared XAML and code compile. It does not navigate, execute useful scripts, or host module web content.
- Desktop-only code is excluded with `#if !BROWSER`; Linux native code normally uses `#if LINUX && !BROWSER`.
- Reflection-based `System.Text.Json` serialization is explicitly enabled in `Dockly.Browser.csproj` so profile, tile, and layout persistence can work in the WASM sandbox.

Do not assume that a native-webview module works in `Dockly.Browser`. Provide an Avalonia fallback UI or treat the module as desktop-only. Browser persistence and file access are limited to APIs available inside the browser sandbox.

---

## 11. Browser and WebView Modules

New web modules should implement `IWebViewModule` in addition to `IModule`. The module declares what it wants to display; Docklys owns the platform engine and integration.

```csharp
public sealed partial class MusicModule : UserControl, IModule, IWebViewModule
{
    public WebViewModuleDefinition WebView { get; } = new()
    {
        StartUrl = "https://music.example.com/",
        AllowedHosts = new[] { "music.example.com", "accounts.example.com" },
        AllowExternalNavigation = true,
        UserAgentProfile = WebViewUserAgentProfile.MobileChromium,
        LinuxEngine = WebViewLinuxEngine.WebKit,
        InitialZoom = 1.0,
        Features = WebViewFeatures.HideScrollbars
            | WebViewFeatures.MiddleMouseDragGuard
            | WebViewFeatures.MobileViewport,
        UsePersistentCookies = true,
        DocumentScripts = new[]
        {
            new WebViewDocumentScript("module-init", "window.__myModuleHost = true;")
        }
    };
}
```

### Definition fields

| Field | Purpose |
|---|---|
| `StartUrl` | Initial document requested by the module. |
| `AllowedHosts` | Hosts that belong to the embedded experience. |
| `AllowExternalNavigation` | Whether destinations outside `AllowedHosts` may leave the module. |
| `UserAgentProfile` | Default, mobile Chromium, or Spotify-specific host behavior. |
| `LinuxEngine` | `WebKit` by default, or the specialized `ChromiumOverlay` path. |
| `InitialZoom` | Initial native engine zoom; must be greater than zero. |
| `Features` | Host-provided scrollbar, viewport, scaling, pointer, and middle-drag behavior. |
| `UsePersistentCookies` | Enables persistent WebKit cookies and the host's auth hardening on Linux. |
| `UseChromiumLoginHandoff` | Allows the specialized Linux Chrome login/cookie-import flow. |
| `DocumentScripts` | Named, module-owned scripts injected at document start. Scripts must be idempotent. |

### Platform engines

| Runtime | Browser implementation |
|---|---|
| Windows desktop | WebView2 through `WebView.Avalonia`; native child geometry and capture are host-managed. |
| Linux desktop | WebKitGTK by default. On X11, a module that requests `ChromiumOverlay` may use a reparented Chrome surface when the host supports it; otherwise it falls back to WebKitGTK. |
| Wayland/XWayland | WebKitGTK remains the safe fallback. The old compositor-controlled Hyprland Chrome route is disabled for normal attachment because an application must not destabilize the compositor. |
| `Dockly.Browser` | No native browser engine; only the compile-time stub exists. |

`WebViewHardeningService` applies to every module webview in Docklys. On Linux it keeps eligible navigation embedded, handles broken `target=_blank` behavior, selects the declared user agent, injects feature/document scripts, persists cookies when requested, and guards known GtkSharp/WebKit lifetime crashes. The marketplace webview is marked host-managed and uses its own policy: `docklys.com` stays embedded, external destinations open in the user's browser, and registry DLL navigation is intercepted for verified installation.

`RunModule` mirrors the Linux WebKit navigation hardening and X11 layout correction so the editor preview behaves like the live dock. Keep the two copies of `Docklys.ModuleContracts/WebViewModule.cs`—one in each repository—in lockstep.

The `ChromiumOverlay`, Spotify user-agent rules, login handoff, and built-in page scripts are specialized compatibility paths, not general-purpose APIs. Prefer the declarative contract and WebKit engine unless a site is proven not to work there.

---

## 12. Common Pitfalls

- **Module doesn't show up in Dockly:** DLL is not in a resolved `Modules/` directory, its signature/developer loading policy rejects it, a runtime dependency is unavailable, its `Type.Name` conflicts, or the class isn't `UserControl` + `IModule`.
- **Module looks broken under different skins:** You hardcoded a color or used `StaticResource`.
- **State resets on tab switch:** You're loading from `ctor` instead of `Loaded`.
- **Instances overwrite each other's state:** Filename doesn't include `UniqueModuleId`.
- **`DynamicResource` returns null:** Key typo or missing from `Dockly/Skins/` files.
- **Webview is blank or floating on Linux:** WebKitGTK is missing, GTK initialized with Wayland instead of X11, or the native child is not attached to the host layout synchronizer.
- **Web module works on desktop but not `Dockly.Browser`:** The WASM target has no native webview. Supply a browser-safe Avalonia fallback.
- **Login or popup leaves the embedded module:** Declare the correct `AllowedHosts`, external-navigation policy, persistent-cookie requirement, and user-agent profile through `IWebViewModule` instead of implementing platform-specific navigation hacks in the module.

---

## 13. AI and Cross-Platform Development

Before an AI coding assistant changes this repository, it **must** read this document and [AIArchitecture.md](AIArchitecture.md). The supported repository instruction entry points are `AGENTS.md` (Codex, Kimi, and Cursor), `CLAUDE.md` (Claude Code), `GEMINI.md` (Gemini CLI), `QWEN.md` (Qwen Code), and `.github/copilot-instructions.md` (GitHub Copilot CLI). Several tools also read `AGENTS.md`, which makes it the shared baseline.

### Verified instruction-file names

Use the files below—not invented model-name files—because these are the instruction locations the corresponding tools discover automatically:

| Tool | Repository instruction file(s) used here |
|---|---|
| Codex | `AGENTS.md` |
| Claude Code | `CLAUDE.md` |
| Gemini CLI | `GEMINI.md` |
| Kimi Code | `AGENTS.md` |
| Qwen Code | `QWEN.md` and `AGENTS.md` |
| GitHub Copilot CLI | `.github/copilot-instructions.md`, plus `AGENTS.md`, `CLAUDE.md`, and `GEMINI.md` |
| Cursor | `AGENTS.md`; `.cursor/rules/*.mdc` is available for scoped rules |
| Cline | `AGENTS.md`; `.clinerules/*.md` is available for additional rules |
| Windsurf Cascade | `AGENTS.md`; `.windsurf/rules/*.md` is available for additional rules |
| Continue | `.continue/rules/docklys-ai.md` |

`AI_INSTRUCTIONS.md` is the shared policy source. `CLAUDE.md`, `GEMINI.md`, `QWEN.md`, and Copilot's instruction file import it using each tool's documented `@path` form; `AGENTS.md` explicitly directs agents to it. Do not add files such as `CODEX.md`, `KIMI.md`, `llm.md`, or `agent.md`: those names are not the automatic repository instruction entry points for these tools.

### Security and engineering references

- [SECURITY.md](SECURITY.md) is authoritative for manifest invariants, capability minimization, automatic tier calculation, untrusted input, secret handling, and security release checks.
- [ENGINEERING_STANDARDS.md](ENGINEERING_STANDARDS.md) is authoritative for library selection, dependency review, cross-platform design, lifecycle safety, and the verification ladder.
- These documents complement this guide. When a change affects permissions, data storage, dependencies, process execution, network behavior, native interop, or platform support, read both before implementation.

### Cross-platform-first development

Treat Windows, macOS, and Linux as the default support target for every new module and editor feature. Do this at the beginning of the work, not as a cleanup step:

1. Design the UI and state flow with Avalonia and .NET APIs that work on all three platforms. Use `Environment.SpecialFolder.ApplicationData` for module data, never hard-coded Windows paths.
2. Isolate native code in small platform-specific services. Guard calls with `OperatingSystem.IsWindows()`, `OperatingSystem.IsMacOS()`, or `OperatingSystem.IsLinux()`; use `#if WINDOWS`, `#if MACOS`, or `#if LINUX` only when compilation itself needs separation.
3. Provide an in-module fallback or clear disabled state when an OS-specific service, executable, or native library is missing. A module must not throw from its constructor merely because a platform feature is unavailable.
4. When adding a dependency, confirm that it supports Windows, macOS, and Linux. If it does not, keep it behind the platform service and preserve the fallback on the other systems.
5. Build the changed project on Windows, macOS, and Linux before release. At minimum run `dotnet build <path-to-project.csproj>` in each environment and manually verify module creation, rendering, theme resources, persistence, and the unavailable-feature fallback.

Do not claim cross-platform support based solely on a Linux or Windows build. Record any deliberate platform limitation in the module README and `SupportedPlatforms`, while retaining safe construction and UI behavior on the other desktop systems.

# Docklys Module Architecture — AI Context

Strict, terse reference for LLMs reading this codebase. All claims are grounded in source paths; verify before acting.

Two repos in this tree:
- `Dockly/` — the host app (Avalonia 11.3, .NET 10 desktop + `net10.0-browser`, cross-platform).
- `DocklysEditor/` — the module dev tool (`RunModule`) + module template (`DefaultModule`) + reference modules + shared contracts (`Docklys.ModuleContracts`).

A *module* is an Avalonia `UserControl` that implements `Docklys.ModuleContracts.IModule`. Distributed as a `<Name>.dll` placed in Dockly's `Modules/` directory.

## Mandatory AI Workflow

Before editing this repository, an AI assistant MUST read `AI_INSTRUCTIONS.md`, `Documentation.md`, `SECURITY.md`, `ENGINEERING_STANDARDS.md`, and this file. The root instruction files for Codex, Claude, Gemini, Kimi, Qwen, and other assistants point to that shared policy set.

For every new module or editor feature, treat Windows, macOS, and Linux as first-class targets from the initial design. Prefer Avalonia/.NET APIs; isolate native calls behind platform services; guard them with `OperatingSystem.IsWindows()`, `OperatingSystem.IsMacOS()`, or `OperatingSystem.IsLinux()`; and provide a non-throwing fallback. Build the changed project on all three operating systems before release. Do not use hard-coded OS paths or let a missing native integration prevent a module from being constructed.

---

## 1. Module Contract (MCCP)

The MCCP (Module Communication & Control Protocol) is the standardized interface set that governs how the Host interacts with a Module.

`Docklys.ModuleContracts/IModule.cs`:

```csharp
public interface IModule {
    string ModuleName { get; }
    string ModuleVersion { get; }
    string UniqueModuleId { get; }
    void SetModuleId(string uniqueModuleId);
    void PrintModuleId();
    int PreferredTileWidth  => 1;   // default 1
    int PreferredTileHeight => 1;
}
```

Reference impl: `DocklysEditor/DefaultModule/DefaultModule.axaml.cs`.
- `UniqueModuleId` is **assigned at runtime by the host** at placement, not by the module. Module stores via `SetModuleId(string)` immediately after instantiation; host reads via `UniqueModuleId`. MUST be used for persistence keys.

Other Core Interfaces (`Docklys.ModuleContracts`):
- **`IResizable`**: Opt-in for runtime tile resizing.
    - `TileResizeRequested`: Event the host subscribes to.
    - `SetTileSize(w, h)`: Host informs module of its current footprint (restored or changed).
- **`IInteractionFreezable`**: For modules with native overlays (e.g., WebView2).
    - `FreezeInteraction()`: Called by host when dragging or showing the settings overlay to prevent the native window from capturing input.
- **`IWebViewModule`** (`Docklys.ModuleContracts/WebViewModule.cs`): Declarative browser-content contract. Module owns URL/policy/page behavior; host owns native engine lifetime, cookies, geometry, capture, navigation hardening, and platform workarounds.
    - `WebViewModuleDefinition`: `StartUrl`, `AllowedHosts`, `AllowExternalNavigation`, `UserAgentProfile`, `LinuxEngine`, `InitialZoom`, `Features`, `UsePersistentCookies`, `UseChromiumLoginHandoff`, `DocumentScripts`.
    - Keep the contract source in `Dockly/Docklys.ModuleContracts/` and `DocklysEditor/Docklys.ModuleContracts/` in lockstep.
    - `WebViewLinuxEngine.WebKit` is the default. `ChromiumOverlay` is an opt-in specialized Linux path with WebKit fallback.
    - `WebViewFeatures`: `HideScrollbars`, `MiddleMouseDragGuard`, `ScaleToFit`, `MobileViewport`, `MouseToTouch`.

---

## 2. Discovery & Module Reload

Owner: `Dockly/Dockly/Modules/ModuleRegistry.cs` (static).

- Static ctor calls `Reload()`. Runs once on first access.
- `Reload(customPath?)` clears caches, then:
  - `LoadBuiltIn()` — scans `Assembly.GetExecutingAssembly()`.
  - `LoadExternal()` — scans every candidate from `ResolveModulesDirectories()`, verifies each DLL, validates runtime dependencies, loads with `Assembly.LoadFrom`, then filters types through `IsValidModule`.
- `CreateModuleInstance(typeName)` → `Activator.CreateInstance(type)` cast to `UserControl`. Returns null on failure.

`ResolveModulesDirectories()` scans distinct candidates in order:
1. `DOCKLY_MODULES_PATH` (env var)
2. `%APPDATA%/Docklys/Modules`
3. `AppDomain.CurrentDomain.BaseDirectory + "Modules"`
4. `Assembly.GetExecutingAssembly().Location + "Modules"`
5. Walk-up looking for source-tree `Modules/` (legacy `CustomModules/` fallback)
6. `%LOCALAPPDATA%/Docklys/Modules`

External host loading is security-gated in current source:
- `ModuleSignature.Verify` rejects unsigned/tampered DLLs unless `DOCKLY_ALLOW_UNSIGNED=1` or `%APPDATA%/Docklys/dev-allow-unsigned` exists.
- User-managed legacy in-process directories are rejected unless `DOCKLY_ALLOW_LEGACY_IN_PROCESS=1` or `%APPDATA%/Docklys/dev-allow-legacy-modules` exists.
- Runtime dependencies are validated before module types are exposed.

**Module Reload (Hot-Reload in RunModule):**
Handled primarily by `RunModule.ModuleLoadContext`.
- **Mechanism**: Custom `AssemblyLoadContext` (ALC) with `isCollectible: true`.
- **Loading**: DLLs are read into a `MemoryStream` and loaded via `LoadFromStream` to avoid file locking on disk.
- **Unloading**: Old `ModuleLoadContext` calls `Unload()`. References dropped, GC collects.
- **Re-instantiation**: New context created, `Activator.CreateInstance` called, host re-assigns `UniqueModuleId` and re-attaches to visual tree.

### Module manifests and capabilities

Every module source folder carries `docklys.manifest.json`:

```json
{
  "schema_version": 1,
  "module_id": "ModuleName",
  "version": "1.0.0",
  "security_tier": 0,
  "requested_capabilities": ["ui.render"]
}
```

- `module_id` follows the module folder/assembly identity; version follows the module version being released.
- `ui.render` is the baseline capability.
- Module settings persistence requires `storage.module.read` and `storage.module.write`, and therefore `security_tier` 1.
- Request the minimum capability set. Do not add permissions for planned work, broad filesystem access, or convenience.
- `RunModule` exposes the active manifest through **Project → Permissions**. The creation dialog starts with module-scoped storage permissions selected; the editor derives the tier from the selected capabilities and writes both to the cloned module's manifest. Developers do not set the tier manually.
- `SECURITY.md` defines the manifest invariants and trust-boundary checks. `ENGINEERING_STANDARDS.md` defines preferred libraries and the portability/verification standard; these are part of the module architecture, not optional contributor notes.

---

## 3. App Lifecycle

`Dockly/Dockly/App.axaml.cs` → `App.Initialize()`:
1. `AvaloniaXamlLoader.Load(this)`
2. `SkinService.LocateSkinsDirectory(AppContext.BaseDirectory)` → installs skin into `Application.Current.Styles`.
3. `ColorSettings.Load().ApplyToResources()`
4. `AppSettings.Load()`

`MainWindow` ctor (`Dockly/Dockly/Views/MainWindow.axaml.cs`):
- Resolves paths for `%APPDATA%/Docklys/tiles.json`, `_profilesDirectory`.
- Hooks `LayoutUpdated` once: reads layout, `GenerateTiles()` → `LoadModuleLayoutForProfile(_activeProfileId)`.

**Cold start = restart.** No persistent runtime state; everything is rebuilt from JSON files.

---

## 4. Profile (Tab) Switch & Transitions

`MainWindow.Profiles.cs:OnProfileButtonClick(profileId)`:
1. Save outgoing profile's placements to disk.
2. `CacheActiveProfileModules()` — store live module instances in memory.
3. Switch `_activeProfileId`.
4. `LoadModuleLayoutForProfile(_activeProfileId)`: re-place cached instances, or instantiate new ones if not in cache.

**Implication:** tab switching does NOT call ctor/`InitializeComponent` again. Avalonia's `Unloaded` / `Loaded` fire, but C# object survives.

**Slide-In / Slide-Out Transitions:**
- **Host Implementation**: The `MainWindow` holds a `TranslateTransform` moving the content horizontally by window width.
- **Phases**: `SlideOutLeft` -> `HoldLeft`, `SlideInFromLeft` -> `HoldCenter`, `SlideOutRight` -> `HoldRight`, `SlideInFromRight` -> `HoldCenter`.
- **Timing**: Managed via `DispatcherTimer` (approx. 16ms intervals) with simple lerp (`dx = (target - current) * 0.2`).

---

## 5. Save States — `%APPDATA%/Docklys/`

All persistence is JSON under `%APPDATA%/Docklys/` (Windows), `~/.config/Docklys/` (Linux), `~/Library/Application Support/Docklys/` (macOS).

- `Settings.json` (Global UI settings)
- `tiles.json` (Profiles data & slots)
- `Profiles/<profileId>_layout.json` (Placements)
- `RunModule.json` (Editor-side skin choice)

**Module-private save data**: NO host-provided settings API exists. Modules **MUST ONLY** save their internal custom settings using the following convention to keep the file system clean:
`%APPDATA%/Docklys/ModuleSaves/<ModuleName>/<UniqueModuleId>.json`

**CRITICAL:** The filename **MUST** include `UniqueModuleId` to avoid collisions between multiple instances of the same module on the grid. Never hardcode to `settings.json`!

**Lifecycle:**
- **Load**: On `Loaded` event (visual tree present).
- **Save**: On user action (e.g., toggle changes). **Avoid saving on `Unloaded`** (prevents disk thrashing during profile switches).

---

## 6. Theme / Skin Architecture

Authoritative host spec lives in `Dockly/CLAUDE.md`.

- **Layers**: App Resources → Active Skin → Color Overrides → AppFont → Module-local Styles.
- **Skin keys**: `Docklys.ModuleContracts/SkinKeys.cs` contains `Color*` brushes, fonts, and `ControlTheme` keys (`ModuleSliderTheme`, etc.).
- **Rule**: Use `{DynamicResource <key>}`. DO NOT hardcode colors or redefine themes. Add layout-only styles locally.
- **Editor mirror**: `DocklysEditor/RunModule/SkinHost.cs` loads skins for preview window.

---

## 7. Platform Targets and Linux Desktop Runtime

### Compile targets/constants

- `Dockly/Dockly.csproj`: `net10.0;net10.0-browser`.
- `Dockly.Desktop`: `net10.0` desktop entry point.
- `Dockly.Browser`: `net10.0-browser` WebAssembly entry point.
- OS constants: `WINDOWS`, `LINUX`, `MACOS`; browser target additionally defines `BROWSER`.
- Native desktop code uses `#if !BROWSER`; Linux native/browser-integration code normally uses `#if LINUX && !BROWSER`.

### Linux startup (`Dockly.Desktop/Program.cs`)

Before Avalonia/GTK loads:
1. Register a per-user `.desktop` entry for portable/IDE installs (`LinuxDesktopEntryService`).
2. Enforce single instance with `Global\\Docklys.SingleInstance`; a second process writes `show-main.cmd` under the app-data `commands/` directory and exits.
3. Set native Unix environment with libc `setenv`:
   - `GDK_BACKEND=x11` (overwrite) because WebView.Avalonia/WebKitGTK embedding requires an XID.
   - `WEBKIT_DISABLE_DMABUF_RENDERER=1` (no overwrite) to avoid XWayland repaint flicker.
4. Optionally refresh consented Hyprland declarative integration; never treat compositor mutation as a module responsibility.
5. Install process/crash diagnostics and terminate residual GTK/WebKit worker threads with `Environment.Exit(0)` after orderly Avalonia shutdown.

Linux app data resolves through `Environment.SpecialFolder.ApplicationData` (normally `~/.config`), so `%APPDATA%/Docklys/...` conventions become `~/.config/Docklys/...`.

### Linux native surfaces

- `X11WebViewOverlaySync`: transform-aware X11 move/resize of WebKit `NativeControlHost` children, content inset, visibility/input shaping, rounded X Shape region.
- `NativeWebViewAnimationSync`: drives native webview/Chromium geometry on the exact dock slide frame because Avalonia `RenderTransform` does not cause native layout.
- `ChromiumSpotifyOverlay`: specialized X11 Chrome surface requested via `WebViewLinuxEngine.ChromiumOverlay`; uses an isolated persistent profile and falls back to WebKit when unavailable.
- `HyprlandChromiumOverlay`: retained integration code, but normal attachment deliberately avoids compositor-driven foreign-toplevel manipulation; application failures must not destabilize Hyprland.
- `SpotifyChromeLoginHandoff`: Wayland/WebKit compatibility flow. Performs login in isolated Chrome, retrieves Spotify cookies over CDP, injects them into the shared WebKit cookie store, then reloads the tile.

`SupportedPlatforms` is metadata found on modules, not part of `IModule`. `RunModule` deliberately previews any constructible module on every developer OS; platform services should degrade safely outside their native OS.

---

## 8. Dockly.Browser (WASM)

Owner: `Dockly.Browser/`.

- Entry point: `BuildAvaloniaApp().WithInterFont().StartBrowserAppAsync("out")`.
- Uses Avalonia single-view/browser lifetime, not an OS `Window`.
- `MainWindow.WindowHelpers.cs` supplies browser-safe `Position`, `Screens`, `Topmost`, visibility, and lifecycle shims so shared UI code compiles.
- No global SharpHook input, tray, native platform handle, X11, WebView2, WebKitGTK, native capture, or child-window overlay.
- `Dockly/Views/WebViewStub.cs` supplies a compile-only `AvaloniaWebView.WebView` API shape. It performs no navigation and `ExecuteScriptAsync` is a no-op.
- `Dockly.Browser.csproj` enables reflection-based `System.Text.Json` (`JsonSerializerIsReflectionEnabledByDefault=true`) because shared profile/tile/layout persistence depends on it.
- A desktop module that requires a native webview is not automatically browser-compatible. It needs an Avalonia fallback or must be treated as desktop-only.

Do not conflate `Dockly.Browser` with the desktop settings marketplace. The former is the entire app compiled to WASM; the latter is a native desktop webview hosted by Docklys.

---

## 9. Desktop Browser / WebView Architecture

### Engine matrix

| Runtime | Implementation |
|---|---|
| Windows desktop | WebView2 via `WebView.Avalonia.Desktop`; native HWND geometry/capture synchronized by host. |
| Linux desktop | WebKitGTK (`libwebkit2gtk-4.0.so.37`) by default; optional specialized X11 Chromium overlay. |
| macOS desktop | WebView.Avalonia platform handler (WKWebView path). |
| `Dockly.Browser` | No native engine; compile-only stub. |

### Host startup and availability

- Desktop registers `UseDesktopWebView()` in `Dockly.Desktop/Program.cs`.
- `SettingsWindow.InitializeWebView()` probes `WebKitDependencyService.IsWebViewAvailable()` before constructing the Linux control; missing WebKitGTK would otherwise hang in the constructor.
- Missing Linux WebKit displays `BuildBrowserFallback()`: install through a detected distro package manager or open the marketplace in the real browser.
- `RunModule/Program.cs` mirrors desktop webview registration via reflection and applies the same native X11/DMA-BUF environment before GTK starts.

### `WebViewHardeningService`

Installed globally from `Dockly/App.axaml.cs`; applies to marketplace and dynamically-created module webviews.

Linux responsibilities:
- Pin GtkSharp wrappers for WebKit-owned transient objects and detach WebView.Avalonia's redundant `DecidePolicy` handler to prevent `g_object_remove_toggle_ref` lifetime crashes.
- For module views, keep declared internal navigation embedded, repair `target=_blank` by navigating the current view, and honor `AllowedHosts` / `AllowExternalNavigation`.
- Select `WebViewUserAgentProfile`; Spotify uses a WebKit-consistent account UA and mobile Chromium player UA.
- Apply declared native zoom.
- Inject document-start host scripts selected by `WebViewFeatures` plus module `DocumentScripts` (`ModuleWebViewScripts`). Scripts must be idempotent.
- When requested, configure persistent SQLite cookies at `%APPDATA%/Docklys/webkit-cookies.sqlite`, accept cross-site cookies, and disable ITP for auth handoff.
- Attach either `ChromiumSpotifyOverlay` or `X11WebViewOverlaySync`.

Marketplace-specific policy:
- Marked host-managed before hardening, so module mobile UA/navigation/layout defaults do not apply.
- `docklys.com` and subdomains stay embedded; external hosts open in the system browser.
- Registry DLL navigations are canceled and passed to the verified managed installer instead of rendering/downloading arbitrary PE bytes in the webview.
- Linux avoids the unsafe/re-entrant `ExecuteScriptAsync` bridge path during GTK navigation; marketplace tile metrics are sent through URL query parameters.

Editor mirror:
- `RunModule/WebViewNavigationHost.cs`: Linux navigation repair, mobile UA, and GtkSharp lifetime guard for previews.
- `RunModule/X11WebViewLayoutSync.cs`: 33 Hz transform-aware X11 geometry and rounded regions for preview zoom/carousel.
- `ModuleLoadContext` shares Avalonia, contracts, `WebView.Avalonia`, and `WebViewCore` assemblies with the default context so runtime-loaded modules retain compatible type identity.

---

## 10. RunModule (Editor) Specifics

- `MainWindow.axaml` — Module header, live preview carousel, Theme library, Testing flyout, and responsive command footer.
- `MainWindow.CreateModule.cs` — Clones `DefaultModule/`, rewrites namespaces/IDs, runs `dotnet sln add`, adds `<ProjectReference>` to `RunModule.csproj`.
- `MainWindow.WebP.cs` — WebP capture composites Avalonia content with native webview snapshots where supported.
- `MainWindow.DocklyLifecycle.cs` / `MainWindow.DocklyPath.cs` — build/install and Docklys process control across desktop platforms.
- `MainWindow.ModuleCatalog.cs` — discovers sibling module projects and loads their freshest DLL into one collectible `ModuleLoadContext` per module.
- `ModuleLoadContext.cs` — loads module bytes from a stream (no DLL lock), resolves private dependencies beside the build output, and shares Avalonia/contracts/webview assemblies with the editor default context.
- Reload detaches instances, unloads the old ALC, loads the fresh DLL, restores the editor-assigned module ID, and re-instantiates.
- The editor does not enforce module `SupportedPlatforms`; constructible modules remain previewable for cross-platform layout/theme work.

---

## 11. Quick Action Map (LLM cheatsheet)

| Goal | File |
|---|---|
| Add a discoverable module | Drop `<Name>.dll` (containing an `IModule` `UserControl`) into the resolved `Modules/`; call `ModuleRegistry.Reload()` if host already started. |
| Make modules theme-aware | Reference keys from `Docklys.ModuleContracts/SkinKeys.cs` via `{DynamicResource}`. |
| Add a new skin | Copy `Dockly/Skins/Default.axaml` → `Dockly/Skins/<Name>.axaml`, preserve every `x:Key` from `SkinKeys`. |
| Add a new skinnable primitive | Add key to `SkinKeys.cs`, add `ControlTheme` to **every** file in `Dockly/Skins/`. |
| Persist module state | `%APPDATA%/Docklys/ModuleSaves/<ModuleName>/<UniqueModuleId>.json` (manual, JSON, write in user-action handlers). |
| Trigger profile-aware re-render | Don't — host handles it. Hook `Loaded` to re-bind visual-tree state on re-attach. |
| Force skin reload | `App.Skins.ApplySkin(name)` (host) or `App.Skins?.ApplySkin(name)` (editor). |
| Declare hosted web content | Implement `IWebViewModule`; return a `WebViewModuleDefinition`. Do not place host/X11/WebKit workarounds in the module. |
| Add Linux page behavior | Prefer `WebViewFeatures` or idempotent `DocumentScripts`; host injects at document start. |
| Diagnose Linux marketplace/webview | Check `libwebkit2gtk-4.0.so.37`, native `GDK_BACKEND=x11`, and `%APPDATA%/Docklys/marketplace-bridge.log`. |
| Add browser/WASM behavior | Guard native code with `!BROWSER`; provide Avalonia-only fallback. Never assume the `WebViewStub` navigates. |

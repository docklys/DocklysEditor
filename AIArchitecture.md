# Docklys Module Architecture — AI Context

Strict, terse reference for LLMs reading this codebase. All claims are grounded in source paths; verify before acting.

Two repos in this tree:
- `Docklys/` — the host app (Avalonia 11.3, .NET 9, MVVM, cross-platform).
- `DocklysModuleEditor/` — the module dev tool (`RunModule`) + module template (`DefaultModule`) + reference module (`VolumeMixer`) + shared contracts (`Docklys.ModuleContracts`).

A *module* is an Avalonia `UserControl` that implements `Docklys.ModuleContracts.IModule`. Distributed as a `<Name>.dll` placed in Dockly's `CustomModules/` directory.

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

Reference impl: `DocklysModuleEditor/DefaultModule/DefaultModule.axaml.cs`.
- `UniqueModuleId` is **assigned at runtime by the host** at placement, not by the module. Module stores via `SetModuleId(string)` immediately after instantiation; host reads via `UniqueModuleId`. MUST be used for persistence keys.

Other Core Interfaces (`Docklys.ModuleContracts`):
- **`IResizable`**: Opt-in for runtime tile resizing.
    - `TileResizeRequested`: Event the host subscribes to.
    - `SetTileSize(w, h)`: Host informs module of its current footprint (restored or changed).
- **`IInteractionFreezable`**: For modules with native overlays (e.g., WebView2).
    - `FreezeInteraction()`: Called by host when dragging or showing the settings overlay to prevent the native window from capturing input.

---

## 2. Discovery & Module Reload

Owner: `Docklys/Dockly/Modules/ModuleRegistry.cs` (static).

- Static ctor calls `Reload()`. Runs once on first access.
- `Reload(customPath?)` clears caches, then:
  - `LoadBuiltIn()` — scans `Assembly.GetExecutingAssembly()`.
  - `LoadExternal()` — `Assembly.LoadFrom(dll)` for every `*.dll` in `ResolveModulesDirectory()`; validates via `IsValidModule`.
- `CreateModuleInstance(typeName)` → `Activator.CreateInstance(type)` cast to `UserControl`. Returns null on failure.

`ResolveModulesDirectory()` checks, in order:
1. `DOCKLY_MODULES_PATH` (env var)
2. `AppContext.BaseDirectory + "CustomModules"`
3. `Assembly.GetExecutingAssembly().Location + "CustomModules"`
4. Walk-up looking for `CustomModules/`
5. `LocalApplicationData/Dockly/CustomModules`

**Module Reload (Hot-Reload in RunModule):**
Handled primarily by `RunModule.ModuleLoadContext`.
- **Mechanism**: Custom `AssemblyLoadContext` (ALC) with `isCollectible: true`.
- **Loading**: DLLs are read into a `MemoryStream` and loaded via `LoadFromStream` to avoid file locking on disk.
- **Unloading**: Old `ModuleLoadContext` calls `Unload()`. References dropped, GC collects.
- **Re-instantiation**: New context created, `Activator.CreateInstance` called, host re-assigns `UniqueModuleId` and re-attaches to visual tree.

---

## 3. App Lifecycle

`Docklys/Dockly/App.axaml.cs` → `App.Initialize()`:
1. `AvaloniaXamlLoader.Load(this)`
2. `SkinService.LocateSkinsDirectory(AppContext.BaseDirectory)` → installs skin into `Application.Current.Styles`.
3. `ColorSettings.Load().ApplyToResources()`
4. `AppSettings.Load()`

`MainWindow` ctor (`Docklys/Dockly/Views/MainWindow.axaml.cs`):
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

**Module-private save data**: NO host-provided settings file. MUST use convention:
`%APPDATA%/Docklys/ModuleSaves/<ModuleName>/<UniqueModuleId>.json`
Filename MUST include `UniqueModuleId` to avoid collisions between instances.

**Lifecycle:**
- **Load**: On `Loaded` event (visual tree present).
- **Save**: On user action. **Avoid saving on `Unloaded`** (prevents disk thrashing during profile switches).

---

## 6. Theme / Skin Architecture

Authoritative spec lives in `Docklys/CLAUDE.md`.

- **Layers**: App Resources → Active Skin → Color Overrides → AppFont → Module-local Styles.
- **Skin keys**: `Docklys.ModuleContracts/SkinKeys.cs` contains `Color*` brushes, fonts, and `ControlTheme` keys (`ModuleSliderTheme`, etc.).
- **Rule**: Use `{DynamicResource <key>}`. DO NOT hardcode colors or redefine themes. Add layout-only styles locally.
- **Editor mirror**: `DocklysModuleEditor/RunModule/SkinHost.cs` loads skins for preview window.

---

## 7. RunModule (Editor) Specifics

- `MainWindow.axaml` — Horizontally-scrollable `ModuleStrip` with overlay arrow buttons.
- `MainWindow.CreateModule.cs` — Clones `DefaultModule/`, rewrites namespaces/IDs, runs `dotnet sln add`, adds `<ProjectReference>` to `RunModule.csproj`.
- `MainWindow.axaml.cs` — **WebP capture** targets `ActiveModule`. **Push to Docklys** copies DLL to Dockly's `CustomModules/`.
- No runtime DLL load in editor strip — uses statically-referenced ProjectReferences for type-safe XAML. Rebuild RunModule to see new modules in strip.

---

## 8. Quick Action Map (LLM cheatsheet)

| Goal | File |
|---|---|
| Add a discoverable module | Drop `<Name>.dll` (containing an `IModule` `UserControl`) into the resolved `CustomModules/`; call `ModuleRegistry.Reload()` if host already started. |
| Make modules theme-aware | Reference keys from `Docklys.ModuleContracts/SkinKeys.cs` via `{DynamicResource}`. |
| Add a new skin | Copy `Dockly/Skins/Default.axaml` → `Dockly/Skins/<Name>.axaml`, preserve every `x:Key` from `SkinKeys`. |
| Add a new skinnable primitive | Add key to `SkinKeys.cs`, add `ControlTheme` to **every** file in `Dockly/Skins/`. |
| Persist module state | `%APPDATA%/Docklys/ModuleSaves/<ModuleName>/<UniqueModuleId>.json` (manual, JSON, write in user-action handlers). |
| Trigger profile-aware re-render | Don't — host handles it. Hook `Loaded` to re-bind visual-tree state on re-attach. |
| Force skin reload | `App.Skins.ApplySkin(name)` (host) or `App.Skins?.ApplySkin(name)` (editor). |

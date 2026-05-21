# Docklys Module Architecture — AI Context

Strict, terse reference for LLMs reading this codebase. All claims are grounded in source paths; verify before acting.

Two repos in this tree:
- `Docklys/` — the host app (Avalonia 11.3, .NET 9, MVVM, cross-platform).
- `DocklysModuleEditor/` — the module dev tool (`RunModule`) + module template (`DefaultModule`) + reference module (`VolumeMixer`) + shared contracts (`Docklys.ModuleContracts`).

A *module* is an Avalonia `UserControl` that implements `Docklys.ModuleContracts.IModule`. Distributed as a `<Name>.dll` placed in Dockly's `CustomModules/` directory.

---

## 1. Module Contract

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

Reference impl: `DocklysModuleEditor/DefaultModule/DefaultModule.axaml.cs` (also has `Id`, `Category`, `Tags`, `TileWidth`, `TileHeight`, `MinAppVersion`, `MaxAppVersion`, `SupportedPlatforms` — these are convention, not enforced by `IModule`).

`UniqueModuleId` is **assigned at runtime by the host**, not by the module. Module stores via `SetModuleId(string)`; host reads via `UniqueModuleId`.

---

## 2. Discovery

Owner: `Docklys/Dockly/Modules/ModuleRegistry.cs` (static).

- Static ctor calls `Reload()`. Runs once on first access.
- `Reload(customPath?)` clears caches, then:
  - `LoadBuiltIn()` — scans `Assembly.GetExecutingAssembly()` for types where `!IsAbstract && UserControl.IsAssignableFrom(t) && implements IModule`.
  - `LoadExternal()` — `Assembly.LoadFrom(dll)` for every `*.dll` in `ResolveModulesDirectory()`; same `IsValidModule` filter.
- `GetAvailableModuleTypes()` → all valid types.
- `CreateModuleInstance(typeName)` → `Activator.CreateInstance(type)` cast to `UserControl`. Returns null on failure (logged to Console).

`ResolveModulesDirectory()` checks, in order, first existing path:
1. `Environment.GetEnvironmentVariable("DOCKLY_MODULES_PATH")`
2. `AppContext.BaseDirectory + "CustomModules"`
3. `Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) + "CustomModules"`
4. Walk-up from `BaseDirectory` looking for a `CustomModules/` folder
5. `LocalApplicationData/Dockly/CustomModules`

If none exist, returns the first non-empty candidate (caller creates it).

**Type lookup:** `GetModuleType(typeName)` matches by `Type.Name` only — module type names must be unique across all loaded assemblies.

**Reload semantics:** No filesystem watcher. `Reload()` is the only refresh path. Add a new DLL → call `ModuleRegistry.Reload()` → host has to re-instantiate placements.

---

## 3. App Lifecycle

`Docklys/Dockly/App.axaml.cs` → `App.Initialize()`:
1. `AvaloniaXamlLoader.Load(this)`
2. `SkinService.LocateSkinsDirectory(AppContext.BaseDirectory)` → `new SkinService(dir)` → `Skins.ApplySkin(AppSettings.Load().SkinName)` — skin must be applied **before** any module XAML resolves `{DynamicResource}` keys.
3. `ColorSettings.Load().ApplyToResources()` — user color overrides layer over skin defaults.
4. `AppSettings.Load()` → applies `GrainStrength`, `FontFamilyName` (sets `AppFont` resource if set).

`OnFrameworkInitializationCompleted()`: `new Views.MainWindow()`, sets tray icon.

`MainWindow` ctor (`Docklys/Dockly/Views/MainWindow.axaml.cs:121`):
- Resolves `_saveFilePath = %APPDATA%/Docklys/tiles.json`, `_profilesDirectory = %APPDATA%/Docklys/Profiles/`. Creates both if missing (`MainWindow.axaml.cs:153-158`).
- Hooks `LayoutUpdated` once: on first layout pass, reads `AppSettings.Load()` for grid sizing, then `GenerateTiles()` → `LoadTileConfiguration()` → `LoadModuleLayoutForProfile(_activeProfileId)`.

**Cold start = restart.** No persistent runtime state; everything is rebuilt from `%APPDATA%/Docklys/*.json` files on every launch. There is no "warm restore" path.

---

## 4. Profile (Tab) Switch — Re-rendering Behavior

A profile is the user's notion of a tab. 5 fixed slots: `profile1`..`profile5` (`MainWindow.Profiles.cs:InitializeProfiles`).

`OnProfileButtonClick(profileId)` (`MainWindow.Profiles.cs`):
1. `SaveModuleLayoutForProfile(_activeProfileId)` — flush the outgoing profile's placements to disk first.
2. `CacheActiveProfileModules()` — store live module instances in `_profileModuleCache[profileId]` so revisiting reuses the same in-memory `UserControl` (no re-instantiation, no state loss).
3. `_activeProfileId = profileId`.
4. `ModuleOverlay.Children.Clear()`.
5. `LoadModuleLayoutForProfile(_activeProfileId)`:
   - If `_profileModuleCache.TryGetValue(profileId, out cached)` → re-place cached instances. **No new Activator.CreateInstance calls.**
   - Otherwise → for each `ModulePlacement` in `profile.ModulePlacements`, `CreateModuleByTypeName(placement.ModuleTypeName)` (calls `ModuleRegistry.CreateModuleInstance`), then `PlaceModuleOnGrid`.
6. `SaveTileConfiguration()` — persists the new active profile.

**Implication for module authors:** tab switching does NOT call ctor/`InitializeComponent` on the second visit of a profile. The module instance is detached from the visual tree and re-attached — Avalonia's `Unloaded` / `Loaded` events fire, but the C# object survives. Persisted state should be reattached in `Loaded`, not the ctor, if it depends on visual-tree presence.

`MainWindow.Persistence.cs:LoadModuleLayoutForProfile` is the canonical reference for this re-attach flow.

---

## 5. Save States — `%APPDATA%/Docklys/`

All persistence is JSON, all paths are `Environment.GetFolderPath(SpecialFolder.ApplicationData) + "Docklys/"`. Cross-platform: Avalonia resolves `ApplicationData` to `%APPDATA%` on Windows, `~/.config` on Linux, `~/Library/Application Support` on macOS.

| File | Owner | Contents |
|---|---|---|
| `Settings.json` | `Dockly.Models.AppSettings` | Global UI settings: skin name, tile scale, background images/opacity/blur, font, grain strength, pattern, window opacity. |
| `tiles.json` | `Dockly.Models.TileSaveData` | `Dictionary<string, ProfileData>` (all 5 profiles) + active profile id + last-saved timestamp + tile slot occupancy. |
| `Profiles/<profileId>_layout.json` | `Dockly.Models.ModuleLayoutData` | Per-profile module placements: `ModuleTypeName`, grid X/Y, width/height in tiles, per-instance settings. |
| `RunModule.json` | `RunModule.SkinHost.PersistedState` | Editor-side: last skin chosen in the RunModule preview window. |

`AppSettings.Load()` returns a default instance on missing-file or deserialize failure. `Save()` ensures the directory exists, writes with `WriteIndented = true`. Both swallow exceptions to Console.

**Module-private save data**: there is NO host-provided per-module settings file. Modules that need to persist their own state must do so themselves, conventionally under:

```
Path.Combine(Environment.GetFolderPath(SpecialFolder.ApplicationData),
             "Docklys", "Modules", <ModuleName>, "settings.json")
```

`Directory.CreateDirectory` before writing. Read on `Loaded` (visual tree is present), write on user action — do not write on `Unloaded` because profile switches detach/re-attach frequently and would thrash the disk.

**Persistence flushes during a session**:
- Profile switch (`MainWindow.Profiles.cs:OnProfileButtonClick`) saves the outgoing profile.
- Layout edits go through `SaveModuleLayoutForProfile` on the affected profile.
- `AppSettings.Save()` is called explicitly by settings dialogs after each user change.
- On app shutdown there is no global "save all" — anything not already on disk is lost.

---

## 6. Theme / Skin Architecture

**Authoritative spec lives in `Docklys/CLAUDE.md`.** This section is a derived summary.

### Layers (outermost → innermost, last writer wins)

1. **App-level `Application.Resources`** (`Dockly/App.axaml`) — fallback brushes used before any skin loads.
2. **Active skin** (`Dockly/Skins/<Name>.axaml`, loaded as `Styles` and inserted into `Application.Current.Styles`) — full set of brushes + `ControlTheme`s for all `SkinKeys`.
3. **Color overrides** (`ColorSettings.ApplyToResources()`) — user-tweaked colors layered on top at startup.
4. **`AppFont` resource** — replaced by `App.Initialize` if user set a font in `AppSettings.FontFamilyName`.
5. **Module-local `UserControl.Styles`** — module's own layout-only styles. Must NOT redefine skin-owned keys.

### Skin loader: `Dockly/Services/SkinService.cs`

- `LocateSkinsDirectory(baseDir)` walks up from `AppContext.BaseDirectory` looking for `Skins/`, `Dockly/Skins/`, `Docklys/Dockly/Skins/`, `Dockly/Dockly/Skins/`. Fallback: a running Dockly process's exe directory. Returns null if nothing found.
- `ListSkins()` → all `*.axaml` filenames (sans extension) in that directory, sorted.
- `ApplySkin(name)`:
  - Picks `name` if available; else `"Default"`; else first in list.
  - Reads file as text, parses via `AvaloniaRuntimeXamlLoader.Parse<Styles>(xaml)`.
  - Removes previously-installed skin from `Application.Current.Styles` (only its slot; FluentTheme + Semi remain).
  - Adds new one. Stores in `_activeSkin` / `_activeSkinName`.
- No filesystem watch. Re-applying requires an explicit `ApplySkin(name)` call from the host (settings dialog).

Editor mirror: `DocklysModuleEditor/RunModule/SkinHost.cs` — same algorithm so the preview window renders modules under the same skin Dockly will use at runtime.

### Resource key contract: `Docklys.ModuleContracts/SkinKeys.cs`

Single source of truth for every published key. **Module XAML must reference these keys, not literals.**

Brushes: `ColorBackground`, `Color2Background`, `Color3Background`, `ColorModuleBackground`, `ColorModuleBorder`, `ColorModuleColor`, `ColorModuleFont`, `ColorModuleAccentColor`, `ColorFont`, `ColorAccent`, `ColorGray`, `ColorKnob`, `ColorBlack`.

Fonts: `AppFont`, `PixelFont`.

ControlThemes: `ModuleSliderTheme`, `ModuleSliderThumbTheme`, `ModuleSliderRepeatTheme`, `ModuleButtonTheme`, `ModuleSquareButtonTheme`.

Style class names: `module-square`, `module-inline-edit`.

### Module-side rules

- **Use `{DynamicResource <key>}` everywhere.** Static resource is wrong: it resolves at parse time, before the skin is installed, and won't re-render on skin swap.
- **Do NOT redefine** any `ControlTheme` listed in `SkinKeys`. Use the host's via `Theme="{DynamicResource ModuleSliderTheme}"`.
- **Do NOT hardcode colors** that should follow the skin. Use the `Color*` brush keys.
- **Class-based opt-in**: `<Button Classes="module-square" />` picks up the skin's `Button.module-square` style.
- **Keep module-local**: layout-only styles (fixed widths tied to the module's tile footprint, MenuItem padding sized to a popup, etc.). Reference `Docklys/CLAUDE.md` and `DocklysModuleEditor/VolumeMixer/VolumeMixer.axaml` as canonical examples.
- **Adding a new key**: edit `SkinKeys.cs` AND every file in `Dockly/Skins/*.axaml`. Skipping any skin file leaves modules with a missing resource error under that skin.

---

## 7. RunModule (Editor) Specifics

`DocklysModuleEditor/RunModule/`:
- `MainWindow.axaml` — horizontally-scrollable `ModuleStrip` (`StackPanel` inside `ScrollViewer`) holding all loaded module instances, with overlay arrow buttons (auto-hidden when nothing to scroll).
- `MainWindow.axaml.cs` — zoom slider scales `ModuleStrip`; WebP capture targets `ActiveModule` (first module in the strip); `Push to Docklys` hardcodes to `VolumeMixer.csproj` and Dockly's `CustomModules/`.
- `MainWindow.ScrollArrows.cs` — `ScrollLeft_Click` / `ScrollRight_Click` drive `ScrollViewer.Offset` with a hand-stepped cubic ease-out. Visibility recomputed in `UpdateScrollArrowVisibility` from `Offset.X`, `Viewport.Width`, `Extent.Width`.
- `MainWindow.CreateModule.cs` — clones `DefaultModule/` to a sibling folder, find-and-replaces `DefaultModule` → `<NewName>` and `BlackModule` → `<NewName>` in all non-binary files, renames `DefaultModule.*` files, runs `dotnet sln <sln> add <proj>`, edits `RunModule.csproj` to add a `<ProjectReference>`. Validation: C# identifier regex, case-insensitive collision against sibling folders, collision against `<NewName>.dll` in Dockly's `CustomModules/`.
- `SkinHost.cs` — editor-side mirror of `SkinService`; persists last-used skin to `%APPDATA%/Docklys/RunModule.json`.

**Module-strip ↔ Create-Module wiring**: a newly-cloned module appears in the strip on next RunModule rebuild — there is no runtime DLL load in the editor (the strip uses statically-referenced ProjectReferences for type-safe XAML namespace imports). Add module → rebuild RunModule → strip shows new module.

---

## 8. Quick Action Map (LLM cheatsheet)

| Goal | File |
|---|---|
| Add a discoverable module | Drop `<Name>.dll` (containing an `IModule` `UserControl`) into the resolved `CustomModules/`; call `ModuleRegistry.Reload()` if host already started. |
| Make modules theme-aware | Reference keys from `Docklys.ModuleContracts/SkinKeys.cs` via `{DynamicResource}`. |
| Add a new skin | Copy `Dockly/Skins/Default.axaml` → `Dockly/Skins/<Name>.axaml`, preserve every `x:Key` from `SkinKeys`. |
| Add a new skinnable primitive | Add key to `SkinKeys.cs`, add `ControlTheme` to **every** file in `Dockly/Skins/`. |
| Persist module state | `%APPDATA%/Docklys/Modules/<ModuleName>/settings.json` (manual, JSON, write in user-action handlers). |
| Trigger profile-aware re-render | Don't — host handles it. Hook `Loaded` to re-bind visual-tree state on re-attach. |
| Force skin reload | `App.Skins.ApplySkin(name)` (host) or `App.Skins?.ApplySkin(name)` (editor). |

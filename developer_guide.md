# Building Modules for Docklys — Developer Guide

Welcome. This guide walks you through everything you need to build a module for Docklys, from "I have an empty folder" to "my module is live in the dock with the right theme, saving its state across restarts." If you'd rather have a terse, machine-grade reference, see [`ai_architecture.md`](./ai_architecture.md) in this same folder.

> **Mental model.** A *module* is a small Avalonia `UserControl` you compile to a single `.dll`. Docklys's main window scans a folder of these DLLs, instantiates the ones it finds, and arranges them on a tiled grid. Your module is a small island of UI that runs inside the host's window — the host owns the shell, the tabs, the skin; you own the contents of your tile.

## Contents

1. [The 30-second tour](#1-the-30-second-tour)
2. [Creating a new module](#2-creating-a-new-module)
3. [The `IModule` contract — what every module must implement](#3-the-imodule-contract)
4. [How Docklys discovers your DLL](#4-how-docklys-discovers-your-dll)
5. [App lifecycle — what happens between launch and your module rendering](#5-app-lifecycle)
6. [Tabs (profiles): the re-render behavior you need to understand](#6-tabs-profiles)
7. [Saving state to `%APPDATA%`](#7-saving-state-to-appdata)
8. [The centralized theme system (skins)](#8-the-centralized-theme-system-skins)
9. [Common pitfalls and how to avoid them](#9-common-pitfalls)

---

## 1. The 30-second tour

```
DocklysModuleEditor/
├── DefaultModule/              ← copy-this-to-start template
├── VolumeMixer/                ← reference module, real-world example
├── Docklys.ModuleContracts/    ← IModule interface + SkinKeys constants
└── RunModule/                  ← the editor: preview your module live

Docklys/Dockly/
├── Skins/Default.axaml         ← the skins your module renders under
├── Modules/ModuleRegistry.cs   ← the loader that finds your DLL
├── CustomModules/              ← drop your built DLL here
└── Views/MainWindow.*.cs       ← the tile grid + profile (tab) system
```

You'll spend almost all of your time in your own module folder. You'll occasionally reference `SkinKeys.cs` (to find the right brush/font name) and a skin file (to understand what your module is rendering against).

---

## 2. Creating a new module

You have three options, in order of preference:

### Option A — Use the "Create Module" button in RunModule (easiest)

1. Open `DocklysModuleEditor/DefaultModule.sln` and run the `RunModule` project.
2. Click **✚ Create Module**.
3. Enter a name (e.g. `Clock`). Letters, digits, underscores; must start with a letter or underscore.
4. The editor:
   - copies the `DefaultModule/` folder to `Clock/`
   - renames `DefaultModule.csproj` → `Clock.csproj`, same for the `.axaml` / `.axaml.cs`
   - rewrites `DefaultModule` → `Clock` and `BlackModule` → `Clock` inside those files (namespace, class, x:Class, `IModule.Id`)
   - runs `dotnet sln add Clock/Clock.csproj`
   - adds a `<ProjectReference>` to `RunModule.csproj`
5. Stop the editor and run it again — your new module appears in the horizontal strip alongside `VolumeMixer` and `DefaultModule`. Use the **◀ ▶** arrows to scroll through them.

### Option B — `dotnet new docklysmodule -n Clock`

`DefaultModule/.template.config/template.json` defines a `dotnet new` template. From the repo root:

```sh
dotnet new --install ./DocklysModuleEditor/DefaultModule
dotnet new docklysmodule -n Clock -o ./DocklysModuleEditor/Clock
```

This gives you the same source layout but with more parameters (`--ModuleDisplayName`, `--ModuleId`, `--TileWidth`, etc.) — useful if you're scripting it.

### Option C — Copy the folder yourself

```sh
cp -r DocklysModuleEditor/DefaultModule DocklysModuleEditor/Clock
```

Then manually rename the files and do a find-and-replace of `DefaultModule` → `Clock`. Add the project to `DefaultModule.sln` and to `RunModule.csproj`'s `<ProjectReference>`s.

Whichever option you pick, the end state is the same: a self-contained folder with one `.csproj`, one `.axaml`, one `.axaml.cs`, building to one `.dll`.

---

## 3. The `IModule` contract

Your `UserControl` must implement `Docklys.ModuleContracts.IModule`:

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

A few important notes:

- **`UniqueModuleId` is set by the host, not by you.** Every instance of your module on the grid is given a unique id at placement time. Store it; use it when you save state so two `Clock`s on the same grid don't clobber each other's settings.
- **`ModuleName` is for display.** Pick something readable. The host uses this in tooltips and the settings dialog.
- **`PreferredTileWidth` / `PreferredTileHeight` are hints**, not constraints. The user may resize your tile. Design your XAML to scale.
- **Look at `DefaultModule.axaml.cs` and `VolumeMixer/VolumeMixer.axaml.cs`** for fuller examples — they include the conventional but non-required `Id`, `Category`, `Tags`, version compatibility, and platform fields that the registry's submission UI reads.

---

## 4. How Docklys discovers your DLL

When Dockly starts, `Dockly.Modules.ModuleRegistry` runs `Reload()`. It does two things:

1. Scans the host assembly for built-in modules (the ones shipped inside Dockly itself, e.g. `BlueModule`, `RedModule`).
2. Scans the **`CustomModules/`** directory and `Assembly.LoadFrom`s every `.dll` it finds, harvesting any types that:
   - aren't abstract,
   - derive from `UserControl`,
   - implement `IModule`.

The `CustomModules/` directory is resolved by trying these paths in order, returning the first that exists:

| Order | Path |
|---|---|
| 1 | `$DOCKLY_MODULES_PATH` (env var) |
| 2 | `AppContext.BaseDirectory / CustomModules/` (next to the exe — the dev default) |
| 3 | `Assembly.GetExecutingAssembly().Location / CustomModules/` |
| 4 | A `CustomModules/` folder found by walking up from `BaseDirectory` |
| 5 | `%LOCALAPPDATA%/Dockly/CustomModules/` (for installed builds) |

### Practical implications

- **Type names must be unique** across all loaded DLLs. `ModuleRegistry.GetModuleType(typeName)` matches by `Type.Name` only — if two modules both define a class called `Clock`, only one wins.
- **There is no filesystem watcher.** Dropping a new DLL into `CustomModules/` while Dockly is running does *not* hot-load it. Either restart Dockly, or wait for a future host call to `ModuleRegistry.Reload()`.
- **Load failures are silent in the UI, logged to Console.** If your module doesn't appear, run Dockly from a terminal and look for `[ModuleRegistry]` lines.

The build target in `DefaultModule.csproj` and `VolumeMixer.csproj` already copies the produced DLL to `OutputModuleDLL/` next to the solution — that's a staging area, not the final destination. You still need to either point Dockly at it (via `DOCKLY_MODULES_PATH`) or copy the DLL into Dockly's `CustomModules/`. The RunModule editor's **⚡ Push to Docklys** button automates this for `VolumeMixer` and you can extend it for your own module.

---

## 5. App lifecycle

Here's exactly what runs between you double-clicking `Dockly.Desktop.exe` and your module rendering.

```
App.Initialize:                                  [from App.axaml.cs]
  1. AvaloniaXamlLoader.Load(this)                       ← parse App.axaml
  2. SkinService.LocateSkinsDirectory(...)               ← find Dockly/Skins/
  3. AppSettings.Load().SkinName                         ← read %APPDATA%/Docklys/Settings.json
  4. Skins.ApplySkin(name)                               ← install skin into Application.Styles
  5. ColorSettings.Load().ApplyToResources()             ← layer user color overrides
  6. GrainBrushFactory.GrainStrength = ...               ← read AppSettings again for visuals

App.OnFrameworkInitializationCompleted:
  7. desktop.MainWindow = new Views.MainWindow()         ← see below
  8. SetupTrayIcon(...)                                  ← system tray

MainWindow ctor:                                 [from MainWindow.axaml.cs]
  9. InitializeComponent()                               ← XAML parsed
 10. resolve _saveFilePath = %APPDATA%/Docklys/tiles.json
     resolve _profilesDirectory = %APPDATA%/Docklys/Profiles/
 11. hook LayoutUpdated (one-shot)

First LayoutUpdated tick:
 12. AppSettings.Load()                                  ← grid size from saved tile scale
 13. GenerateTiles()                                     ← build the empty grid
 14. LoadTileConfiguration()                             ← read tiles.json
 15. LoadModuleLayoutForProfile(activeProfileId)         ← read Profiles/<id>_layout.json
       └─ for each placement:
             ModuleRegistry.CreateModuleInstance(typeName)   ← YOUR CTOR runs here
             PlaceModuleOnGrid(instance, x, y, w, h)         ← attaches to visual tree
```

Three things to lock in your head:

- **The skin is installed before your module's XAML is parsed.** That means `{DynamicResource ColorAccent}` lookups in your AXAML succeed from the very first render. You don't have to defend against "no skin yet" — there is always a skin by the time your ctor runs.
- **Your ctor runs lazily, when the host needs an instance to place.** If no saved layout places your module, your ctor never runs. The host doesn't pre-instantiate everything just because the DLL is loaded.
- **There is no warm restore.** Every launch is a cold start that rebuilds state from JSON files on disk. If your module is showing up "almost right but with last session's stale state," the bug is in your save/load code, not in some hidden in-memory cache.

---

## 6. Tabs (profiles)

Docklys's bottom buttons (`Profile 1` … `Profile 5`) are tabs. Each profile has its own set of modules placed at its own coordinates. When the user clicks a different profile button, the host:

1. **Saves the outgoing profile** to `Profiles/<oldId>_layout.json`.
2. **Caches the live module instances** (`_profileModuleCache[oldProfileId] = ...`) — your `UserControl` objects are stashed, not destroyed.
3. **Clears the visual tree** of the outgoing profile's tiles.
4. **Loads the incoming profile**. If its instances are already in the cache (because the user visited it earlier in this session), they're re-attached — **your ctor does not run again**. Otherwise the host calls `ModuleRegistry.CreateModuleInstance` to make fresh ones.

### What this means for you

- **Avalonia's `Loaded` and `Unloaded` events fire on every tab switch**, because your control gets detached and re-attached to the visual tree. Use these — not the constructor — for setup that depends on being in the visual tree.
- **Your C# object state survives tab switches in the same session.** A timer you started in the ctor will still be ticking when the user comes back to your tab. (Good for clocks; bad if you're holding a file handle.)
- **A full app restart wipes the cache.** Don't rely on in-memory state surviving across sessions — persist anything important to disk.

A safe pattern:

```csharp
public Clock() {
    InitializeComponent();
    this.Loaded   += OnLoaded;
    this.Unloaded += OnUnloaded;
}

private void OnLoaded(object? sender, RoutedEventArgs e) {
    // Re-bind to things that depend on the visual tree being live.
    // Restore module state from disk if you haven't already this session.
    if (!_stateLoaded) { LoadState(); _stateLoaded = true; }
    StartTimer();
}

private void OnUnloaded(object? sender, RoutedEventArgs e) {
    StopTimer();
    // Do NOT auto-save here — profile switches detach/re-attach frequently
    // and would thrash the disk. Save on user action.
}
```

---

## 7. Saving state to `%APPDATA%`

All host persistence goes through `Environment.GetFolderPath(SpecialFolder.ApplicationData) + "/Docklys/"`. Avalonia resolves that to:

| OS | Path |
|---|---|
| Windows | `C:\Users\<you>\AppData\Roaming\Docklys\` |
| macOS | `~/Library/Application Support/Docklys/` |
| Linux | `~/.config/Docklys/` |

The host writes a handful of files there:

| File | Purpose |
|---|---|
| `Settings.json` | Global UI: active skin, tile scale, fonts, background images, opacity. |
| `tiles.json` | All 5 profiles + the active profile id + tile slots. |
| `Profiles/<profileId>_layout.json` | Per-profile module placements. |
| `RunModule.json` | Editor-side: the last skin you picked in RunModule's preview. |

### Persisting your own module's state

There is no host-provided per-module settings API. You roll it yourself, but **the convention is explicit** so settings don't end up scattered across the filesystem:

```csharp
private static readonly string SettingsPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "Docklys", "ModuleSaves", "Clock", $"{ModuleId}.json");

private void SaveState() {
    Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
    var json = JsonSerializer.Serialize(new ClockState {
        TwentyFourHour = _is24Hour,
        ShowSeconds = _showSeconds,
    }, new JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(SettingsPath, json);
}

private void LoadState() {
    if (!File.Exists(SettingsPath)) return;
    var state = JsonSerializer.Deserialize<ClockState>(File.ReadAllText(SettingsPath));
    if (state == null) return;
    _is24Hour    = state.TwentyFourHour;
    _showSeconds = state.ShowSeconds;
}
```

Notice three things:

1. **The path includes `UniqueModuleId`.** Two `Clock`s on the same grid have different IDs, so they save independently. If you keyed the file by class name only, the second instance would overwrite the first.
2. **`Directory.CreateDirectory` before writing.** It's a no-op if the directory exists; it's mandatory if it doesn't.
3. **Save in user-action handlers** (toggle clicked, value changed, dialog OK'd), not in `Unloaded`. Tab switches will hammer your disk if you save on unload.

### What NOT to use for persistence

- **The `Application.Current.Resources`** dictionary — that's the in-memory resource lookup table; it doesn't survive a restart.
- **The host's `AppSettings.json`** — that's for global UI, not module-specific data. Adding properties there would couple your module to host releases.
- **A file inside your DLL's output directory** — Dockly's DLL folder is read-only on installed builds, and your file would be wiped on update.

---

## 8. The centralized theme system (skins)

The single most important rule for a Docklys module:

> **Do not ship inline styles for primitives the host already themes.**
> Sliders, buttons, menu items, fonts, and colors come from the active skin. You only style things that are *intrinsically* about your module — your overall layout, your tile footprint, anything that doesn't make sense to retheme.

This is enforced by convention, not by the compiler, and it's the easiest place for a new module to go off the rails.

### How it works

The host loads exactly one skin from `Docklys/Dockly/Skins/<Name>.axaml` at startup and installs it into `Application.Current.Styles`. The user can swap skins at runtime from the settings dialog; the host removes the old skin's `Styles` and adds the new one's. Modules that look up resources via `DynamicResource` re-render instantly. Modules that used `StaticResource` or hardcoded values do not — and will look broken under any skin other than the one their author tested.

The full list of keys the host promises lives in `Docklys.ModuleContracts/SkinKeys.cs`. **Reference those constants in your XAML.** The compile-time check is that `SkinKeys.ColorAccent == "ColorAccent"` — so if you typo the resource key in XAML you'll find out at runtime, but you can paste from the source-of-truth file.

### Doing it right

```xaml
<UserControl xmlns="https://github.com/avaloniaui"
             x:Class="Clock.Clock">
    <Border Background="{DynamicResource ColorModuleColor}"
            CornerRadius="10" Padding="6">
        <StackPanel>
            <TextBlock Text="12:34"
                       Foreground="{DynamicResource ColorModuleFont}"
                       FontFamily="{DynamicResource AppFont}" />
            <Slider Theme="{DynamicResource ModuleSliderTheme}"
                    Orientation="Horizontal" Minimum="0" Maximum="60" />
            <Button Classes="module-square" Content="Reset" />
        </StackPanel>
    </Border>
</UserControl>
```

That module renders correctly under `Default.axaml`, `Industrial.axaml`, and any future skin somebody else adds — without your code knowing what skins exist.

### Doing it wrong

```xaml
<!-- ❌ Hardcoded colors -->
<Border Background="#363737" />

<!-- ❌ Static resource — won't re-render on skin swap -->
<Border Background="{StaticResource ColorModuleColor}" />

<!-- ❌ Redefining a theme the skin owns -->
<UserControl.Resources>
    <ControlTheme x:Key="ModuleSliderTheme" TargetType="Slider"> ... </ControlTheme>
</UserControl.Resources>

<!-- ❌ Inline slider chrome -->
<Slider>
    <Slider.Styles>
        <Style Selector="Slider /template/ Thumb">
            <Setter Property="Background" Value="Red" />
        </Style>
    </Slider.Styles>
</Slider>
```

Each of those breaks one of the skin's invariants. Reviewers will reject submissions that do them.

### What you *should* style locally

Anything tied to your module's footprint: fixed widths, your own paddings, a menu flyout sized to match your tile, your own custom controls that the host doesn't theme. `DocklysModuleEditor/VolumeMixer/VolumeMixer.axaml`'s `UserControl.Styles` block is the canonical example — it tunes menu sizing to the 110×94 footprint without touching any skin-owned theme.

### Adding a new skinnable thing

If you genuinely need a new themable primitive — say a rotary knob you'd want skinned alongside sliders — the right move is to extend the skin contract, not to ship a local override:

1. Add the new key (e.g. `ModuleKnobTheme`) to `Docklys.ModuleContracts/SkinKeys.cs`.
2. Add a `ControlTheme x:Key="ModuleKnobTheme"` entry to **every** file in `Docklys/Dockly/Skins/`. Skipping any skin leaves modules using the new key broken under that skin.
3. Reference it from your module via `Theme="{DynamicResource ModuleKnobTheme}"`.

That's a host-side change, so it lands in `Docklys/`, not in your module folder. The project's own `Docklys/CLAUDE.md` documents the same flow at the source.

---

## 9. Common pitfalls

**"My module doesn't show up in Dockly."**
Run Dockly from a terminal and look for `[ModuleRegistry]` lines. Usually one of: DLL not in `CustomModules/`, DLL has a `Type.Name` that already exists in another module, your class isn't `UserControl` + `IModule`, or your ctor throws.

**"My module looks fine under the Default skin but broken under Industrial."**
You hardcoded a color or used `StaticResource`. Search your `.axaml` for `#`, `Color="`, and `StaticResource`; replace with `{DynamicResource <SkinKeys key>}`.

**"My module's state resets on every tab switch."**
You're saving from your ctor or `Unloaded` and loading from your ctor. The ctor doesn't run on tab re-attach; `Loaded` does. Move load to `Loaded` (guarded by a "once per session" flag) and save in user-action handlers.

**"Two instances of my module overwrite each other's saved state."**
Your file path doesn't include `UniqueModuleId`. Add it.

**"`{DynamicResource ColorModuleFont}` returns null at runtime."**
Either the key isn't in `SkinKeys.cs` (typo), or it isn't in every skin in `Dockly/Skins/`. Open `Default.axaml` and confirm the `x:Key` matches your usage exactly.

**"My module compiles standalone but RunModule doesn't show it in the carousel."**
RunModule statically references modules via `<ProjectReference>` in `RunModule.csproj`. The **✚ Create Module** button adds the reference for you; if you scaffolded a module by hand, add the reference manually and rebuild RunModule.

**"The Push to Docklys button doesn't push my module, only VolumeMixer."**
That button is hardcoded to `VolumeMixer.csproj` in `RunModule/MainWindow.axaml.cs:FindVolumeMixerProject`. For your own module, build it with `dotnet build`, then either copy the resulting DLL into Dockly's `CustomModules/` manually or extend that handler.

---

## Where to look next

- **A complete, real-world module:** `DocklysModuleEditor/VolumeMixer/VolumeMixer.axaml(.cs)` — uses the skin system properly, persists state, handles menus, multiple instances.
- **The host-side rules:** `Docklys/CLAUDE.md` — the authoritative spec for skin and theming.
- **The compact LLM reference:** `ai_architecture.md` next to this file.
- **The host's discovery / lifecycle code:** `Docklys/Dockly/Modules/ModuleRegistry.cs`, `Docklys/Dockly/Views/MainWindow.Persistence.cs`, `Docklys/Dockly/Views/MainWindow.Profiles.cs`.

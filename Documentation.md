# Building Modules for Docklys — Documentation

Welcome. This guide walks you through everything you need to build a module for Docklys, from "I have an empty folder" to "my module is live in the dock with the right theme, saving its state across restarts."

> **Mental model.** A *module* is a small Avalonia `UserControl` you compile to a single `.dll`. Docklys's main window scans a folder of these DLLs, instantiates the ones it finds, and arranges them on a tiled grid. Your module is a small island of UI that runs inside the host's window.

For a terse, machine-grade reference, see [`ai_architecture.md`](./ai_architecture.md). For Editor specifics, see [`README.md`](./README.md).

## Contents

1. [The 30-Second Tour](#1-the-30-second-tour)
2. [Creating a New Module](#2-creating-a-new-module)
3. [The `IModule` Contract & MCCP](#3-the-imodule-contract--mccp)
4. [Discovery & Module Reload](#4-discovery--module-reload)
5. [App Lifecycle](#5-app-lifecycle)
6. [Tabs (Profiles) & Re-rendering](#6-tabs-profiles--re-rendering)
7. [Saving State to `%APPDATA%`](#7-saving-state-to-appdata)
8. [The Theme System (Skins)](#8-the-theme-system-skins)
9. [Common Pitfalls](#9-common-pitfalls)

---

## 1. The 30-Second Tour

```text
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

---

## 2. Creating a New Module

You have three options to create a module:

### Option A — "Create Module" Button in RunModule (Easiest)
1. Open `DefaultModule.sln` and run the `RunModule` project.
2. Click **✚ Create Module**.
3. Enter a name (e.g. `Clock`).
4. Restart the editor to see your module in the carousel.

### Option B — `dotnet new docklysmodule`
From the repo root:
```sh
dotnet new --install ./DocklysModuleEditor/DefaultModule
dotnet new docklysmodule -n Clock -o ./DocklysModuleEditor/Clock
```

### Option C — Manual Copy
Copy the `DefaultModule` folder manually, rename everything from `DefaultModule` to your module name, add it to `DefaultModule.sln`, and add a `<ProjectReference>` in `RunModule.csproj`.

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

---

## 4. Discovery & Module Reload

When Dockly starts, `ModuleRegistry` scans for built-in modules and external DLLs.

**Where to put your `.dll`:**
1. `$DOCKLY_MODULES_PATH` (env var)
2. Next to the executable in `CustomModules/`
3. `%LOCALAPPDATA%/Dockly/CustomModules/`

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

## 9. Common Pitfalls

- **Module doesn't show up in Dockly:** DLL not in `CustomModules/`, `Type.Name` conflict, or your class isn't `UserControl` + `IModule`.
- **Module looks broken under different skins:** You hardcoded a color or used `StaticResource`.
- **State resets on tab switch:** You're loading from `ctor` instead of `Loaded`.
- **Instances overwrite each other's state:** Filename doesn't include `UniqueModuleId`.
- **`DynamicResource` returns null:** Key typo or missing from `Dockly/Skins/` files.

# Docklys Module Lifecycle & MCCP — LLM Context

Strict reference for Module Reload, Transitions, and Persistence.

## 1. MCCP (Module Communication & Control Protocol)
The MCCP is the standardized interface set that governs how the Host (Dockly/RunModule) interacts with a Module.

### Core Interfaces (`Docklys.ModuleContracts`)
- **`IModule`**: Identity and lifecycle.
    - `UniqueModuleId`: Assigned by the host at placement. MUST be used for persistence keys.
    - `SetModuleId(id)`: Called by the host immediately after instantiation.
- **`IResizable`**: Opt-in for runtime tile resizing.
    - `TileResizeRequested`: Event the host subscribes to.
    - `SetTileSize(w, h)`: Host informs module of its current footprint (restored or changed).
- **`IInteractionFreezable`**: For modules with native overlays (WebView2).
    - `FreezeInteraction()`: Called by host when dragging or showing the settings overlay to prevent the native window from capturing input.

---

## 2. Module Reload (Hot-Reload)
Handled primarily by `RunModule.ModuleLoadContext`.

- **Mechanism**: Custom `AssemblyLoadContext` (ALC) with `isCollectible: true`.
- **Loading**: DLLs are read into a `MemoryStream` and loaded via `LoadFromStream` to avoid file locking on disk.
- **Unloading**: 
    1. The old `ModuleLoadContext` is called with `Unload()`.
    2. References to the old module types are dropped.
    3. GC collects the ALC (collectible).
- **Re-instantiation**:
    1. A new `ModuleLoadContext` is created for the fresh DLL.
    2. `Activator.CreateInstance` creates a new instance of the module.
    3. Host re-assigns the `UniqueModuleId` and re-attaches to the visual tree.

---

## 3. Slide-In / Slide-Out Transitions
Visual transitions when switching profiles (tabs) or previewing in the editor.

- **Host Implementation**: The `MainWindow` (TopLevel) holds the `RenderTransform`.
- **Animation**: A `TranslateTransform` moving the content horizontally by the window width (`w`).
- **Phases (`SlidePreviewButton`)**:
    1. `SlideOutLeft` -> `HoldLeft`
    2. `SlideInFromLeft` -> `HoldCenter`
    3. `SlideOutRight` -> `HoldRight`
    4. `SlideInFromRight` -> `HoldCenter`
- **Timing**: Managed via `DispatcherTimer` (approx. 16ms intervals) with a simple lerp for smoothness (`dx = (target - current) * 0.2`).

---

## 4. AppData Saves (Persistence)
Clean, isolated storage for module-specific settings.

- **Root Path**: `%APPDATA%/Docklys/` (Windows) or `~/.config/Docklys/` (Linux).
- **Module Storage**: `%APPDATA%/Docklys/ModuleSaves/<ModuleName>/`
- **Instance Persistence**: Filename MUST include `UniqueModuleId` to avoid collisions between multiple instances of the same module type.
    - Convention: `%APPDATA%/Docklys/ModuleSaves/<ModuleName>/<UniqueModuleId>.json`
- **Lifecycle**:
    - **Load**: On `Loaded` event (visual tree present).
    - **Save**: On user action (toggle/slider change). **Avoid saving on `Unloaded`** to prevent disk thrashing during profile switches.
- **Shared Data**:
    - `tiles.json`: Global placement and profile data.
    - `Settings.json`: Global UI settings (Skin, Scale, etc.).

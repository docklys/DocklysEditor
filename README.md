# Docklys Module Editor (`RunModule`)

Welcome to the Docklys Module Editor! This guide is tailored for **AI-assisted developers and end-users** who want to quickly build, test, and deploy modules without diving deep into the technical architecture. If you are writing advanced code manually, refer to the [Documentation](Documentation.md) and [AI Architecture](ai_architecture.md) for deeper insights.

## Overview

The `DocklysModuleEditor` solution provides a sandbox where you can develop modules in isolation before deploying them to the main Docklys application. 

It contains:
- **`RunModule`**: The editor application itself.
- **`DefaultModule`**: A template module used when scaffolding new modules.
- **`VolumeMixer`**: A real-world reference module to learn from.
- **`Docklys.ModuleContracts`**: The shared interfaces and theme constants every module uses.

## Editor Interface & Features

When you launch the `RunModule` project, you will see a comprehensive toolbar and preview area. Here is what every button does:

### Top Bar (Identity & View)
- **✎ Rename:** Rename the currently active module inline.
- **⊡ Size:** Adjust the `PreferredTileWidth` and `PreferredTileHeight` of the active module.
- **✕ Hold to delete:** Press and hold this button to charge a deletion, then confirm to completely remove the module from the solution.
- **Zoom Slider:** Scales the module strip visually.
- **⧉ Dual:** Toggles Dual View, showing two instances of the module side-by-side to test cross-instance state synchronization.
- **↔ Slide:** Loops the Docklys slide-in/slide-out animation so you can preview how your module behaves during tab transitions.
- **Skin Selector:** Test your module against different Docklys themes (e.g., Default, Industrial).

### Preview Area
- **Module Carousel:** A horizontally scrollable strip displaying your modules.
- **◀ / ▶ (Scroll Arrows):** Navigate left and right through the available modules.

### Bottom Bar (Lifecycle, Deploy & Artifacts)
- **✚ New:** Scaffolds a brand new module by cloning `DefaultModule` and linking it to the solution.
- **↺ Reload:** Re-instantiates the active module via AssemblyLoadContext. Useful for testing cold-start initialization and clearing memory without restarting the editor entirely.
- **🎨 Theme:** Opens an overlay to customize and test global theme colors dynamically.
- **⚡ Push to Docklys:** Compiles the active module and copies its `.dll` directly into your local Docklys `Modules/` folder.
- **Save .webp:** Captures a transparent WebP screenshot of the active module for promotional assets.
- **📂 .webp:** Opens the folder containing your saved screenshots.
- **📂 .dll:** Opens the folder containing the compiled module DLLs.
- **📂 Modules:** Opens the local Docklys `Modules/` directory.
- **📂 Saves:** Opens the AppData folder where modules save their custom settings.

## AppData & Saving Settings

Modules are **only** allowed to save their internal custom settings in a specific AppData directory. The editor's **📂 Saves** button opens this directory for you.

The exact path convention your code must use is:
`%APPDATA%\Docklys\ModuleSaves\<ModuleName>\<UniqueModuleId>.json`

*Note for AI Coders: Ensure you include `UniqueModuleId` in the filename to prevent multiple instances of the same module from overwriting each other's settings.*

## Tutorial: Setting Up Your First Module

Follow these steps to create your first module from scratch:

1. **Open the Solution:** Open `DocklysModuleEditor/DefaultModule.sln` in your IDE.
2. **Run the Editor:** Set the `RunModule` project as your startup project and run it.
3. **Scaffold the Module:** Click the **✚ New** button in the bottom left. Enter a valid name (e.g., `ClockModule`).
4. **Restart the Editor:** Once the scaffold completes, close the editor and run it again. Your new module is now a real project in the solution and will appear in the carousel!
5. **Develop with AI:** Edit the `.axaml` and `.axaml.cs` files inside your new module's folder. 

## Best Practices & Publishing

- **Publishing via Editor:** Use **⚡ Push to Docklys** to instantly test your module in the live Docklys application.
- **Defining Widths:** Make sure to set `PreferredTileWidth` and `PreferredTileHeight` properly so Dockly can adapt them for design accuracy.
- **Theme Support:** Always use `{DynamicResource <Key>}` from `SkinKeys.cs` instead of hardcoding colors.
- **Publishing to Web:** You can publish and share your created modules on the official [Docklys Website](https://docklys.qwqc.de/DocklysEditor). When creating a module via the editor, you can also select an open-source license which will be automatically generated for you!

# Docklys Module Editor (`RunModule`)

Welcome to the Docklys Module Editor! This is the primary development environment for building, previewing, and testing modules for Docklys.

## Overview

The `DocklysModuleEditor` solution provides a sandbox where you can develop modules in isolation before deploying them to the main Docklys application. 

It contains:
- **`RunModule`**: The editor application itself.
- **`DefaultModule`**: A template module used when scaffolding new modules.
- **`VolumeMixer`**: A real-world reference module to learn from.
- **`Docklys.ModuleContracts`**: The shared interfaces and theme constants every module uses.

## Editor Interface & Features

When you launch the `RunModule` project, you will be presented with the Editor Window. Here is a breakdown of every button and logic in the editor:

- **Module Carousel (Module Strip):** A horizontally scrollable strip displaying all loaded modules in the solution.
- **◀ / ▶ (Scroll Arrows):** Navigate left and right through the available modules in the strip. These arrows auto-hide when scrolling is unnecessary.
- **Zoom Slider:** Scales the module strip visually, allowing you to test how your module renders at different UI scales.
- **Skin Selector (Dropdown):** Test your module against different Docklys themes (e.g., Default, Industrial, etc.). The editor loads the exact same `SkinKeys` that the main app uses, ensuring 100% fidelity.
- **📷 Capture WebP (Screenshot):** Automatically captures a WebP screenshot of the currently active (focused) module. This is extremely useful for generating promotional assets for your module.
- **✚ Create Module:** Scaffolds a brand new module. It clones the `DefaultModule`, renames namespaces and classes to your chosen name, automatically runs `dotnet sln add` to link it to the solution, and adds it as a `<ProjectReference>` to the editor.
- **⚡ Push to Docklys:** Compiles the active module and copies its resulting `.dll` directly into the local Docklys `CustomModules/` directory.

## Tutorial: Setting Up Your First Module

Follow these steps to create your first module from scratch:

1. **Open the Solution:** Open `DocklysModuleEditor/DefaultModule.sln` in your IDE.
2. **Run the Editor:** Set the `RunModule` project as your startup project and run it.
3. **Scaffold the Module:** Click the **✚ Create Module** button in the top bar. Enter a valid name (e.g., `ClockModule`).
4. **Restart the Editor:** Once the scaffold completes, close the editor and run it again. Your new module is now a real project in the solution and will appear in the carousel!
5. **Develop:** Edit the `.axaml` and `.axaml.cs` files inside your new module's folder. The editor statically references modules, so you get full XAML designer support and type safety.

## Best Practices & Notes

- **Define Widths Properly:** Make sure to define `PreferredTileWidth` and `PreferredTileHeight` in your module class so Dockly can adapt them properly for design accuracy.
- **Theme Support:** Always use `{DynamicResource <Key>}` from `SkinKeys.cs` instead of hardcoding colors or styles.
- **Publishing:** When publishing your module (e.g., as a NuGet package or downloadable DLL), ensure it includes an icon, a descriptive name, and a proper license.

For a comprehensive guide on architecture, state persistence, and theming, please read the [Documentation](Documentation.md) and [AI Architecture](ai_architecture.md).

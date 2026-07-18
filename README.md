# Docklys Module Editor (`RunModule`)

Welcome to the Docklys Module Editor! This guide is tailored for **AI-assisted developers and end-users** who want to quickly build, test, and deploy modules without diving deep into the technical architecture. If you are writing advanced code manually, refer to the [Documentation](Documentation.md) and [AI Architecture](AIArchitecture.md) for deeper insights.

## Overview

The `DocklysModuleEditor` solution provides a sandbox where you can develop modules in isolation before deploying them to the main Docklys application. 

It contains:
- **`RunModule`**: The editor application itself.
- **`DefaultModule`**: A template module used when scaffolding new modules.
- **`VolumeMixer`**: A real-world reference module to learn from.
- **`Docklys.ModuleContracts`**: The shared interfaces and theme constants every module uses.

## AI, Security, and Cross-Platform Policy

AI coding assistants working in this repository must read [Documentation.md](Documentation.md) and [AIArchitecture.md](AIArchitecture.md) before changing module or editor code. The repository-level AI instruction files (`AGENTS.md`, `CLAUDE.md`, `GEMINI.md`, and their companion files) repeat this requirement for the common coding assistants.

Every module has a `docklys.manifest.json` beside its project. It declares the module identity, version, security tier, and only the capabilities the module needs. `ui.render` is always required. A module that reads or writes its own per-instance settings must request `storage.module.read` and `storage.module.write` at `security_tier` 1. Use **Project → Permissions** in the editor to review or change these fields; the New Module dialog starts with module-storage permissions selected as a useful template foundation.

Keep module project files declarative: custom MSBuild targets, `Exec` tasks, and build-time copy/deployment actions are forbidden. Docklys collects normal Release output and host tooling performs deployment.

Modules should work on Windows, macOS, and Linux from the beginning. Use Avalonia and .NET cross-platform APIs first, keep native code behind operating-system checks, and provide a safe fallback when a native integration is unavailable. Before shipping, build and test the module on all three desktop operating systems; the detailed checklist is in [Documentation.md](Documentation.md#cross-platform-first-development).

## Documentation Map

- [Documentation.md](Documentation.md) — module authoring, manifests, persistence, web modules, and the cross-platform checklist.
- [AIArchitecture.md](AIArchitecture.md) — host/editor lifecycle, module discovery, reload behavior, and platform architecture.
- [SECURITY.md](SECURITY.md) — manifest invariants, automatic tier calculation, trust boundaries, dependency review, and security release checks.
- [ENGINEERING_STANDARDS.md](ENGINEERING_STANDARDS.md) — preferred .NET/Avalonia libraries, error handling, portability, and verification standards.
- [AI_INSTRUCTIONS.md](AI_INSTRUCTIONS.md) — the mandatory workflow for AI coding tools.
- [MODULE_APPROVAL.md](MODULE_APPROVAL.md) — public-safe module publication checklist, package allowlist, and vulnerability-verdict requirements.

## Editor Interface & Features

When you launch the `RunModule` project, you will see a comprehensive toolbar and preview area. Here is what every button does:

### Top Bar (Module & View)

- **GitHub repository:** Creates or updates the repository for the active module.
- **New module:** Scaffolds a module by cloning `DefaultModule` and adding the new project to the solution.
- **Zoom slider:** Scales the live module preview. The percentage label also shows the selected tiles-per-row layout when the zoom is snapped to a real Docklys layout.
- **Dock tile layout:** Cycles through the real Docklys layouts, from three to eight tiles per row, and updates the zoom slider.
- **Rename:** Renames the active module and updates its project references.
- **Tile size:** Changes the module's preferred tile width and height.
- **Hold delete:** Press and hold to charge the deletion action, then confirm removal of the module from the solution.

### Preview Area

- **Live preview:** Displays the active module in an isolated preview surface.
- **Left and right arrows:** Navigate through the available modules. The arrows sit inside the preview without obscuring its border.

### Bottom Bar (Build, Test & Project Tools)

The commands appear in this order:

1. **Build & install:** Builds the active module and copies its DLL into the configured Docklys `Modules` directory.
2. **Reload:** Re-instantiates the active module through its load context, which is useful for testing initialization without restarting the editor.
3. **Docklys:** Starts or stops the Docklys application.
4. **Theme library:** Opens the preview theme overlay. Choose an installed Docklys skin, edit preview colors, or reset saved color overrides.
5. **Save preview:** Captures a WebP preview image of the active module.
6. **Testing:** Contains:
   - **Show two modules:** Displays two independent instances side by side to test cross-instance synchronization.
   - **Play slide animation:** Loops the Docklys slide-in and slide-out transition.
7. **Files:** Captures Docklys or opens preview images, build output, installed modules, and module saves.
8. **Project:** Opens module information, license selection, and publishing tools.
   It also contains **Permissions**, which edits `docklys.manifest.json` for the active module.
9. **Help & legal:** Opens the Terms of Service, Privacy Policy, and support pages.

## AppData & Saving Settings

Modules are **only** allowed to save their internal custom settings in a specific AppData directory. Use **Files → Open module saves** to open this directory.

The exact path convention your code must use is:
`%APPDATA%\Docklys\ModuleSaves\<ModuleName>\<UniqueModuleId>.json`

*Note for AI Coders: Ensure you include `UniqueModuleId` in the filename to prevent multiple instances of the same module from overwriting each other's settings.*

## Tutorial: Setting Up Your First Module

Follow these steps to create your first module from scratch:

1. **Open the Solution:** Open `DocklysModuleEditor/DefaultModule.sln` in your IDE.
2. **Run the Editor:** Set the `RunModule` project as your startup project and run it.
3. **Scaffold the Module:** Click **New module** in the top bar and enter a valid name, such as `ClockModule`.
4. **Load the Module:** Allow the editor to create and build the project. The new module will then appear in the preview carousel.
5. **Develop with AI:** Edit the `.axaml` and `.axaml.cs` files inside your new module's folder. 

## Best Practices & Publishing

- **Publishing via Editor:** Use **Build & install** to build the module and copy it into Docklys for live testing.
- **Defining Widths:** Make sure to set `PreferredTileWidth` and `PreferredTileHeight` properly so Dockly can adapt them for design accuracy.
- **Theme Support:** Always use `{DynamicResource <Key>}` from `SkinKeys.cs` instead of hardcoding colors.
- **Publishing to Web:** You can publish and share your created modules on the official [Docklys Website](https://docklys.qwqc.de/DocklysEditor). When creating a module via the editor, you can also select an open-source license which will be automatically generated for you!

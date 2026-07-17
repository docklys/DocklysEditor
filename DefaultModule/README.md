# Default Module

`DefaultModule` is the minimal Docklys module template used by the Module Editor when creating a new module. It provides a one-by-one Avalonia tile and the required `IModule` implementation without adding any behaviour or dependencies beyond the Docklys contracts.

## Use it as a starting point

1. Copy or scaffold this module through `RunModule`.
2. Rename `DefaultModule.axaml`, `DefaultModule.axaml.cs`, the project file, and the namespace to match the new module name.
3. Update the metadata in `DefaultModule.axaml.cs`, especially `Id`, `ModuleName`, `Category`, tags, supported platforms, and tile dimensions.
4. Build the module and install the resulting DLL through the editor.

The `Id` must be unique. The host supplies `UniqueModuleId` through `SetModuleId`; use it in the filename of any per-instance settings you save.

## Project layout

- `DefaultModule.axaml` — starter Avalonia view.
- `DefaultModule.axaml.cs` — `IModule` metadata and module control.
- `docklys.json` — package metadata used by Docklys.
- `DefaultModule.csproj` — .NET 9 project; copies the built DLL to `../OutputModuleDLL`.

## Build

```bash
dotnet build DefaultModule/DefaultModule.csproj
```

For guidance on module metadata, theme resources, settings storage, and deployment, see the repository [README](../README.md) and [Documentation](../Documentation.md).

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
- `docklys.manifest.json` — security tier and requested capabilities. The template starts with module-scoped storage permissions at tier 1; remove them through **Project → Permissions** if the module remains stateless.
- `DefaultModule.csproj` — declarative .NET 9 project; the collector reads its standard Release output.

## Build

```bash
dotnet build DefaultModule/DefaultModule.csproj
```

For guidance on module metadata, permissions, cross-platform behavior, theme resources, settings storage, and deployment, see the repository [README](../README.md), [Documentation](../Documentation.md), [AI Architecture](../AIArchitecture.md), [Security Policy](../SECURITY.md), and [Engineering Standards](../ENGINEERING_STANDARDS.md).

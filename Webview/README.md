# Webview

`Webview` is a resizable Docklys media tile that displays a user-configured web page. New instances start at Google and retain their URL separately for each Docklys module instance.

## Features

- Opens a web page in the Docklys web-view host.
- Lets the user change the tile width and height from its settings overlay.
- Remembers the selected URL at `Docklys/ModuleSaves/Webview/<UniqueModuleId>.json` inside the platform application-data directory.
- Responds to host interaction freezing and keeps the native web view aligned during tile resizing, zooming, and dock animation.

## Requirements

- .NET 8 SDK to build.
- A Docklys host installation that includes `Avalonia.WebView.dll` and its runtime dependencies. The module discovers those assemblies at runtime so it can still load in the editor; without them it displays an in-tile error.

## Project layout

- `Webview.axaml` — tile UI and settings overlay.
- `Webview.axaml.cs` — module metadata, URL persistence, resizing, and web-view hosting logic.
- `WinWebViewHost.cs` — Windows native-host helpers.
- `docklys.json` — package metadata.

## Build

```bash
dotnet build Webview/Webview.csproj
```

The build copies the DLL to `../OutputModuleDLL` and, when the target directories exist, also attempts to update Docklys' installed module directories. Use the Module Editor's **Build & install** command for the normal deployment workflow.

## Module metadata

The module is in the `Media` category, supports Windows, Linux, and macOS, and prefers a 2 × 3 tile. Its size can be changed through the module UI.

For shared module conventions, see the repository [README](../README.md).

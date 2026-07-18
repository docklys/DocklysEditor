# Spotify

`spotify` is a resizable Docklys media tile that opens the Spotify web player at `https://open.spotify.com/` in the Docklys web-view host.

## Features

- Starts directly on the Spotify web player.
- Provides an in-tile settings overlay for changing its width and height.
- Declares `IWebViewModule` metadata so the host can apply the Spotify user-agent profile, mobile viewport, scale-to-fit behaviour, hidden scrollbars, mouse-to-touch input, and middle-mouse drag protection.
- Keeps the native web-view surface synchronized during resizing, preview zooming, dock animation, and interaction freezing.

## Requirements

- .NET 8 SDK to build.
- A Docklys host that provides `Avalonia.WebView.dll` and its runtime dependencies. The module loads that integration by reflection; in an environment without it, the module shows an explanatory error instead of failing to load.
- A Spotify account is required to use Spotify's web player. Spotify controls and playback availability are governed by Spotify.

## Project layout

- `spotify.axaml` — tile UI and resize settings overlay.
- `spotify.axaml.cs` — module metadata, Spotify web-view definition, and native-host synchronization.
- `WinWebViewHost.cs` — Windows native-host helpers.
- `docklys.json` — package metadata.

## Build

```bash
dotnet build spotify/spotify.csproj
```

The build leaves the DLL in the standard project output. Use **Build & install** in `RunModule` for deployment; the project contains no build-time deployment or copy action.

## Module metadata

The module is in the `Media` category, supports Windows, Linux, and macOS, and prefers a 2 × 3 tile. Its size can be changed from the module UI.

For shared module conventions, see the repository [README](../README.md).

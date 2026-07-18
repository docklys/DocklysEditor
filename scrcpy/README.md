# scrcpy

`scrcpy` mirrors and controls a connected Android device from a Docklys tile. The module renders decoded frames and forwards user input through a reviewed Docklys host contract. The Docklys host, not the module, owns the native mirroring implementation.

## Features

- Starts an embedded scrcpy server on a connected Android device.
- Displays the phone screen with aspect-ratio-preserving letterboxing.
- Sends touch events from pointer input and releases active touches when capture is lost.
- Sends keyboard, text, and scroll-wheel input through the scrcpy control channel.
- Streams at up to 1024 pixels maximum dimension, 60 FPS, and 8 Mbps by default.

## Requirements

- .NET 9 SDK to build.
- Android Debug Bridge (`adb`) available to the module process.
- An Android device with USB debugging enabled and authorized for the host computer.

The bundled `Assets/scrcpy-server` must remain version-compatible with `AdbClient.ServerVersion`; update both together when updating the server.

## Project layout

- `scrcpy.axaml` — phone-screen tile layout.
- `scrcpy.axaml.cs` — module control, input mapping, and frame rendering.
- `Contracts/` — reviewed contract source submitted with the module.
- `scrcpy.axaml.cs` — module UI, input mapping, module-data boundary, and host-service consumption.

The host implementation owns ADB communication, server deployment, H.264 decoding, control transport, native helper assets, validation, and per-run consent. Those implementation details do not ship in this module.
- `LICENSE.txt` — third-party scrcpy license information.

## Build

```bash
dotnet build scrcpy/scrcpy.csproj
```

The build embeds the server in the module's normal build output. Use **Build & install** in `RunModule` to deploy it to Docklys; the project contains no custom MSBuild deployment target.

## Module metadata

The module supports Windows, Linux, and macOS and prefers a 3 × 5 tile. It implements `IResizable`, although its current `SetTileSize` implementation does not persist a resized layout.

For shared module conventions, see the repository [README](../README.md).

## Approval evidence

`scrcpy` uses the pinned `Avalonia` and `Avalonia.Desktop` packages defined in the repository [module approval allowlist](../MODULE_APPROVAL.md). Before publishing, run the transitive package audit and build command from that guide.

The Docklys host communicates with a user-authorized Android device through a locally available ADB installation and local transport. It validates tool availability before starting and the module presents a status message when the host feature, ADB, `ffmpeg`, or the device connection is unavailable. No credentials, private-device data, or approval-system details belong in submission evidence.

The module itself does not launch ADB or FFmpeg and does not open sockets. It consumes the reviewed `Docklys.ModuleContracts` source included under `Contracts/`; a Docklys host that publishes contract version `1.1.0` supplies the runtime device-mirroring service, consent gate, native-tool validation, and platform fallback.

# scrcpy

`scrcpy` mirrors and controls a connected Android device from a Docklys tile. It embeds the scrcpy server in the module, connects to the device through ADB, decodes the H.264 video stream, and forwards pointer, wheel, keyboard, and text input to Android.

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
- `Native/AdbClient.cs` — ADB communication and embedded-server deployment.
- `Native/MirrorSession.cs` — session lifecycle and video/control coordination.
- `Native/H264Decoder.cs` — H.264 frame decoding.
- `Native/ControlChannel.cs` — Android input commands.
- `Native/ScrcpyConnection.cs` — scrcpy socket connection.
- `Assets/scrcpy-server` — embedded scrcpy server binary.
- `LICENSE.txt` — third-party scrcpy license information.

## Build

```bash
dotnet build scrcpy/scrcpy.csproj
```

The build embeds the server in the DLL and copies the module DLL to `../OutputModuleDLL`. Use **Build & install** in `RunModule` to deploy it to Docklys.

## Module metadata

The module supports Windows, Linux, and macOS and prefers a 3 × 5 tile. It implements `IResizable`, although its current `SetTileSize` implementation does not persist a resized layout.

For shared module conventions, see the repository [README](../README.md).

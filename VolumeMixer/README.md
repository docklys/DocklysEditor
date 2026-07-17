# Volume Mixer

`VolumeMixer` is a one-by-one Docklys quick-tools tile for selecting active application audio sessions and changing their volume. It is the repository's reference module for an interactive native integration.

## Features

- Discovers active audio sessions and assigns one to each slider.
- Controls Windows audio sessions through NAudio.
- Controls Linux PulseAudio or PipeWire-Pulse sessions through `pactl`.
- Resolves application icons where available.
- Keeps sliders for the same application synchronized across live Volume Mixer instances.
- Uses Docklys dynamic theme resources for the module background, foreground, and accent color.

## Requirements

- Windows, or Linux with `pactl` available. PipeWire installations are supported when they expose the PulseAudio-compatible `pipewire-pulse` service.
- .NET 8 SDK to build.

No audio controls are shown when no supported audio-session backend is available.

## Project layout

- `VolumeMixer.axaml` — tile layout, slider controls, and styles.
- `VolumeMixer.axaml.cs` — UI behaviour, session assignment, and synchronization.
- `AudioSessions.cs` — Windows and Linux audio-session backends.
- `LinuxIconResolver.cs` — Linux application-icon lookup.
- `docklys.json` — package metadata.

## Build

```bash
dotnet build VolumeMixer/VolumeMixer.csproj
```

The project copies its DLL to `../OutputModuleDLL`. Use **Build & install** in `RunModule` to deploy it to Docklys.

## Module metadata

The module is named **Volume Mixer**, is in the `QuickTools` category, and supports Windows and Linux. Its tile size is fixed at 1 × 1.

For shared module conventions, see the repository [README](../README.md).

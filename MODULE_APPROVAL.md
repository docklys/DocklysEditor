# Docklys Module Approval Foundations

This is the public, high-level approval contract for a Docklys module. It deliberately records the evidence an author must provide without reproducing private reviewer operations, internal routing, credentials, or bypasses. The approval service remains the final authority.

Read this with [SECURITY.md](SECURITY.md) and [ENGINEERING_STANDARDS.md](ENGINEERING_STANDARDS.md) before publishing a module.

## Approval baseline

A submission is review-ready only when it has all of the following:

- A valid `docklys.manifest.json` beside the module. Its identity and version match the artifact, its capabilities are minimal, and its tier is editor-calculated.
- A small, cross-platform design. Native APIs and external tools are optional, guarded by the operating system and availability checks, and have a clear non-throwing fallback.
- Per-instance state stored in its approved module-owned location, with malformed external data handled safely. Ordinary settings use `ModuleSaves`; an approved host integration may use the constrained local-app-data support-data boundary documented in `SECURITY.md`.
- A complete inventory of direct third-party packages. Every package is pinned, appears in the allowlist below, has a concrete purpose, and has a current vulnerability verdict.
- A declarative project file. Custom MSBuild `Target` blocks, build-time `Exec` tasks, and project-defined file-copy/deployment steps are forbidden. The collector reads the standard Release build output; deployment belongs to host tooling.
- A clean `dotnet list <project> package --vulnerable --include-transitive` result, plus a build of the module. Run the relevant lifecycle and fallback checks on Windows, macOS, and Linux before release.
- No credentials, tokens, cookies, private paths, user data, or approval-system details in the source, artifact, logs, or documentation.

Do not add an undeclared capability, package, native executable, downloaded binary, process invocation, raw socket, or external host to make a review pass. Public modules may consume only narrow host contracts for privileged operations; the Docklys host owns user consent, native tools, transports, validation, lifecycle, and safe fallbacks.

## Third-party NuGet allowlist

The following is the reviewed package baseline for the current Avalonia 11.3 release line. A package outside this list is **not approved** until its owner documents the requirement, license, support on each target OS, transitive/native assets, and a fresh advisory result here.

| Package | Pinned version | Approved scope | Vulnerability verdict |
|---|---:|---|---|
| `Avalonia` | `11.3.18` | Module UI and shared contracts | Pass — no known advisory reported by the repository-wide transitive audit on 2026-07-18. |
| `Avalonia.Desktop` | `11.3.18` | Desktop module/editor startup integration | Pass — no known advisory reported by the repository-wide transitive audit on 2026-07-18. |
| `Avalonia.Controls.ColorPicker`, `Avalonia.Diagnostics`, `Avalonia.Fonts.Inter`, `Avalonia.Markup.Xaml.Loader`, `Avalonia.Skia`, `Avalonia.Themes.Fluent` | `11.3.18` | Editor and preview tooling only | Pass — same audit. |
| `FluentIcons.Avalonia` | `2.0.319` | Editor and preview icons | Pass — same audit. |
| `Semi.Avalonia`, `Semi.Avalonia.ColorPicker` | `11.2.1.8` | Editor/theme tooling only | Pass — same audit. |
| `SkiaSharp`, `SkiaSharp.NativeAssets.Win32`, `SkiaSharp.NativeAssets.Linux` | `2.88.9` | Editor/theme rendering; include platform native assets only where used | Pass — same audit. |
| `MessageBox.Avalonia` | `3.1.5.1` | Editor dialogs only | Pass — same audit. |
| `WebView.Avalonia`, `WebView.Avalonia.Desktop` | `11.0.0.1` | Host-managed editor preview only | Pass — same audit. |
| `NAudio` | `2.2.1` | `VolumeMixer` Windows audio backend only; runtime guarded | Pass — same audit. |
| `Newtonsoft.Json` | `13.0.3` | `VolumeMixer` legacy JSON parsing only | Pass — same audit. |
| `System.Drawing.Common` | `7.0.0` | `VolumeMixer` Windows icon conversion only; never execute its path on Linux or macOS | Pass — same audit. |

The verdict is evidence at a point in time, not a permanent safety claim. Re-run the audit after changing a package, restoring against a different feed, or preparing a release. `Directory.Build.props` makes advisories at low severity or above build-stopping warnings across this repository.

## Submitting approval evidence

Keep the evidence concise and public-safe:

1. State the module version, manifest capabilities, calculated tier, target platforms, and fallback behavior.
2. List direct packages by ID and exact version, referring to this allowlist. Explain every exception before adding it.
3. Include the result of the transitive vulnerability audit and the affected-project build. Do not paste secrets, full user paths, or raw diagnostic payloads.
4. For a module that consumes a host integration backed by a native helper or user-installed executable, state only the user-visible requirement, OS guards, host-owned validated boundary, consent behavior, and fallback. Do not publish operational review mechanics or sensitive configuration.
5. Confirm that the project file contains no custom MSBuild execution. Build only with SDK standard targets and submit the normal Release output.

For `scrcpy`, the public-facing evidence is: a user-authorized Android device and the host-provided device-mirroring contract. Docklys owns ADB, FFmpeg, local transport, validation, and per-run consent. The module must show an understandable status message when the host feature or a dependency is unavailable; it must not request a process or network capability.

## AI checklist

Before an AI changes a module, it must:

1. Read this file and the shared security/engineering policy.
2. Keep the manifest least-privilege and let the editor derive its tier.
3. Prefer .NET/Avalonia APIs, guard native behavior on Windows, macOS, and Linux, and retain a safe fallback.
4. Refuse to add an unallowlisted package or downgrade a reviewed package without a new review record and successful advisory audit.
5. Never add a custom MSBuild target, `Exec` task, or build-time copy/deployment action to a module project.
6. Keep processes, raw sockets, and direct HTTP out of public modules; use only a narrow approved host contract for privileged work.
7. Build the affected project, run its package audit, validate manifests, and explicitly report platforms that were not tested.

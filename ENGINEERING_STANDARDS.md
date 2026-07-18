# Docklys Engineering Standards

Use this document when selecting libraries, writing module code, or evaluating a change. The goal is a minimal, secure, cross-platform dependency surface—not the largest possible toolkit.

## 1. Default technology choices

| Need | Preferred choice | Why / boundary |
|---|---|---|
| UI, layout, theme resources | Avalonia already referenced by the project | Keeps rendering and resource behavior consistent across Windows, macOS, and Linux. Use host `DynamicResource` skin keys rather than a competing UI framework. |
| Module state and manifests | `System.Text.Json` | Built into modern .NET, UTF-8 native, and suited to small typed JSON documents. Use typed DTOs and validation; never use unsafe binary serialization. |
| Files and paths | `System.IO`, `Path`, `Environment.SpecialFolder.ApplicationData` | Cross-platform path handling without hard-coded OS locations. |
| Logging | `Microsoft.Extensions.Logging` when structured logging is needed | Prefer structured event properties and redaction. Do not introduce a logging package for a few local diagnostics. |
| HTTP | `HttpClient` with explicit timeout, cancellation, host validation, and bounded response handling | Add `Microsoft.Extensions.Http.Resilience` only when an actual remote integration needs retry/circuit-breaker policies. Retries must not repeat non-idempotent actions by default. |
| Concurrency | `async`/`await`, `CancellationToken`, `Task` | Keep UI updates on the Avalonia UI thread; cancel stale work on module unload/reload. |
| Native integration | A narrow platform service behind `OperatingSystem` checks | Keep native libraries and process control optional, isolated, and replaceable with a safe fallback. |

Do not add a library because it is fashionable, because another stack uses it, or to solve a problem the runtime already solves.

## 2. Design rules

### Module boundaries

- A module is an `IModule` `UserControl`; it owns its UI and module-scoped state, while the host owns discovery, native webview lifetime, docking, profiles, skins, and global policy.
- Keep constructor work cheap and non-failing. Attach visual work in the appropriate lifecycle event and release cancellable work when the module unloads.
- Use `UniqueModuleId` for state isolation. Never use a static mutable setting as the only source of per-instance state.

### Error handling

- Validate at every trust boundary: JSON, filesystem data, network payloads, URLs, reflection, process output, and native interop results.
- Catch expected operational exceptions close to the operation, report a safe diagnostic, and preserve a usable UI.
- Do not swallow exceptions silently when recovery is impossible; log a redacted error and expose a user-meaningful state.
- Avoid broad catch blocks around security decisions. Failed validation is a denial, not a reason to continue.

### Performance and resource use

- Reuse `JsonSerializerOptions` and `HttpClient` instances when applicable.
- Debounce rapid user-driven persistence and avoid blocking the UI thread on file, network, or process work.
- Dispose streams, subscriptions, timers, processes, and native handles deterministically.
- Do not introduce polling or background work without cancellation, visibility/lifecycle ownership, and a measured need.

## 3. Cross-platform delivery standard

For every feature, record this design review before implementation:

| Question | Required answer |
|---|---|
| Does it use an Avalonia/.NET API that works on Windows, macOS, and Linux? | If no, isolate it behind a platform service. |
| What happens on an unsupported OS or missing native dependency? | A clear fallback/disabled UI; no constructor failure. |
| Are paths, process invocation, and user data portable? | Use `Path`, application-data folders, and argument APIs. |
| Does the package ship required native assets on every target? | Verify before adding it. |
| How will it be validated? | Build and exercise the changed project on all three OSes. |

## 4. Verification ladder

Run the smallest relevant checks first, then expand for risk:

1. Parse and validate edited JSON/manifest files.
2. Build the affected project with `dotnet build <project>.csproj`.
3. Run focused automated tests where they exist.
4. Exercise the editor/module lifecycle: create, load, render, reload, save, and restore.
5. Verify Windows, macOS, and Linux behavior, including missing-native-service fallback.
6. Review the final diff for new permissions, secrets, dependencies, platform-specific code, and documentation drift.

## 5. Dependency decision record

Every newly added external package should have a short note in the pull request or change description:

- Requirement it satisfies and why the existing stack is insufficient.
- Target-framework and Windows/macOS/Linux support.
- License, update health, and advisory review.
- Runtime assets that must travel with a module DLL.
- Fallback behavior if the package/native component is unavailable.

This prevents dependency accumulation and makes security review repeatable.

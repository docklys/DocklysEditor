# Docklys AI Development Handbook

This is the shared policy source for AI coding tools. Before proposing or making a change, read these documents in order:

1. [README.md](README.md) — project map and editor workflow.
2. [Documentation.md](Documentation.md) — module-authoring contract and implementation guidance.
3. [AIArchitecture.md](AIArchitecture.md) — host/editor architecture and lifecycle constraints.
4. [SECURITY.md](SECURITY.md) — threat model, manifest policy, data handling, and release checks.
5. [ENGINEERING_STANDARDS.md](ENGINEERING_STANDARDS.md) — approved defaults for dependencies, quality, and portability.
6. [MODULE_APPROVAL.md](MODULE_APPROVAL.md) — publication evidence, package allowlist, and safe approval guidance; required for module publication, package changes, native helpers, and approval failures.

If documents conflict, stop and report the conflict. Do not silently choose the less secure or less portable interpretation.

## Operating model

1. Inspect the relevant implementation, project file, tests, and existing conventions before editing.
2. State the smallest safe change that satisfies the request. Preserve unrelated user changes.
3. Design Windows, macOS, and Linux support before writing code. Native behavior belongs behind a small platform service with an Avalonia/.NET fallback.
4. Implement with least privilege, deterministic state handling, and explicit failure paths.
5. Validate in proportion to risk, then report changed files, validation performed, warnings, and remaining platform-specific checks.

## Non-negotiable security rules

- Every module needs a `docklys.manifest.json`. Keep `module_id` aligned with the module folder/assembly and keep the manifest version aligned with the released module version.
- Request the smallest capability set. Every visible module uses `ui.render`. Only modules that persist their own settings request `storage.module.read` and/or `storage.module.write`.
- The editor calculates `security_tier`; authors never set it. Storage capability means tier 1; UI-only means tier 0. Never hand-edit a tier to bypass this mapping.
- Never request a capability for speculative future work. Change permissions only through **Project → Permissions** or the documented manifest schema.
- Persist only module-owned data under `ModuleSaves/<ModuleName>/<UniqueModuleId>.json`. Treat all loaded JSON and external input as untrusted; handle malformed data without crashing the module.
- Never log secrets, tokens, cookies, full private paths, or user content. Never commit credentials, generated secrets, or local configuration.
- Do not weaken signature verification, module loading policy, navigation allowlists, sandboxing, or dependency checks to make a feature work.

## Cross-platform rules

- Treat Windows, macOS, and Linux as first-class desktop targets from the first design step.
- Prefer Avalonia, `System.IO`, `System.Text.Json`, and other cross-platform .NET APIs. Never hard-code `%APPDATA%`, drive letters, shell paths, registry keys, or platform-specific separators.
- Guard platform APIs with `OperatingSystem.IsWindows()`, `OperatingSystem.IsMacOS()`, or `OperatingSystem.IsLinux()`. Use compile-time constants only when the compilation unit itself differs.
- A module must construct, render, and show an understandable fallback when an optional executable, native library, or OS API is unavailable.
- Before release, build on Windows, macOS, and Linux. Verify rendering, dynamic skin resources, independent instance settings, and unavailable-native-feature fallbacks on each platform.

## Quality gates

Before declaring a change complete:

- Build the affected project and run its relevant tests or explain why a check cannot run.
- Verify every edited JSON file parses and every manifest has a calculated tier consistent with its capabilities.
- Check for regressions in state isolation, theme resources, dependency loading, and platform fallbacks.
- For new dependencies, document why the built-in option is insufficient, review support on all desktop targets, pin a compatible version, and check the advisory state.
- For module publication, package changes, native helpers, or approval failures, follow `MODULE_APPROVAL.md`; never add a package outside its allowlist without a documented review and fresh vulnerability verdict.
- Update the relevant README, `Documentation.md`, `AIArchitecture.md`, `SECURITY.md`, or `ENGINEERING_STANDARDS.md` whenever behavior, security, or contributor workflow changes.

## Communication requirements

- Be precise about facts versus assumptions.
- Report blocked platform validation explicitly; a successful build on one OS is not evidence of support on the other two.
- Ask for direction before broadening permissions, introducing a native-only dependency, changing public contracts, or deleting user data.

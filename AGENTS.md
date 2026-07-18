# Docklys Repository Instructions

Read [AI_INSTRUCTIONS.md](AI_INSTRUCTIONS.md) before any change. Then read [Documentation.md](Documentation.md), [AIArchitecture.md](AIArchitecture.md), [SECURITY.md](SECURITY.md), and [ENGINEERING_STANDARDS.md](ENGINEERING_STANDARDS.md) relevant to the task.

For module publication, package changes, native helpers, or approval failures, also read [MODULE_APPROVAL.md](MODULE_APPROVAL.md).

## Required behavior

- Keep every module manifest least-privilege; security tier is calculated from capabilities, never selected manually.
- Preserve per-instance state isolation and use only module-scoped storage.
- Design, build, and validate Windows, macOS, and Linux behavior from the start; guard native code and provide safe fallbacks.
- Prefer existing project conventions and cross-platform .NET/Avalonia APIs. Add a dependency only with a documented need, cross-platform support, pinned version, and advisory review.
- Keep module project files declarative: do not add custom MSBuild targets, `Exec` tasks, or build-time copy/deployment actions.
- Build and test the affected project before completion. Report validation and any untested platform explicitly.
- Never weaken security checks, expose credentials, delete user data, or overwrite unrelated work without explicit authorization.

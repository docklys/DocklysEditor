---
name: Docklys project instructions
alwaysApply: true
---

Before editing, read `AI_INSTRUCTIONS.md`, `Documentation.md`, and `AIArchitecture.md`. For module publication, package changes, native helpers, or approval failures, also read `MODULE_APPROVAL.md`.

- Keep module manifests least-privilege and let the editor calculate their security tier from requested capabilities.
- Treat Windows, macOS, and Linux as first-class targets; use guarded native services and safe fallbacks.
- Build and validate changed projects on all three desktop operating systems before release.
- Do not add an unallowlisted package or downgrade a reviewed package without the evidence required by `MODULE_APPROVAL.md`.
- Do not add custom MSBuild targets, `Exec` tasks, or build-time copy/deployment actions to module projects.

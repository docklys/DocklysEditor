---
name: Docklys project instructions
alwaysApply: true
---

Before editing, read `AI_INSTRUCTIONS.md`, `Documentation.md`, and `AIArchitecture.md`.

- Keep module manifests least-privilege and let the editor calculate their security tier from requested capabilities.
- Treat Windows, macOS, and Linux as first-class targets; use guarded native services and safe fallbacks.
- Build and validate changed projects on all three desktop operating systems before release.

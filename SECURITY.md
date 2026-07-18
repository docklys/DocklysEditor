# Docklys Module Security Policy

This document defines the security boundary for Docklys modules and the Module Editor. It applies to contributors, module authors, and AI coding tools.

## 1. Security goals

- A module receives only the permissions it demonstrably needs.
- One module instance cannot overwrite another instance's data.
- Missing native dependencies or unsupported OS services fail safely and visibly.
- External content, persisted state, and downloaded data are treated as untrusted.
- Development convenience never disables production security by default.

## 2. Manifest is the permission contract

Each module folder contains `docklys.manifest.json`:

```json
{
  "schema_version": 1,
  "module_id": "ExampleModule",
  "version": "1.0.0",
  "security_tier": 1,
  "requested_capabilities": [
    "ui.render",
    "storage.module.read",
    "storage.module.write"
  ]
}
```

### Required invariants

- `schema_version` is `1`.
- `module_id` matches the module folder and assembly identity.
- `version` matches the version being shipped.
- `requested_capabilities` is the complete, minimal set—not an aspirational list.
- `ui.render` is the baseline for visible modules.
- Storage capabilities are scoped to the module's own persisted state only.

### Tier calculation

The editor owns tier calculation. It derives the value every time it writes a manifest:

| Requested capability set | Calculated tier |
|---|---:|
| `ui.render` only | 0 |
| Either `storage.module.read` or `storage.module.write` | 1 |
| Both storage capabilities | 1 |

Do not present a tier picker, hand-edit a tier, or retain an old higher tier after storage capability is removed. Add a new mapping only when the manifest schema formally defines the corresponding capability.

## 3. Data protection

### Module state

- Store module settings only under `Docklys/ModuleSaves/<ModuleName>/<UniqueModuleId>.json` in the platform application-data directory.
- Include `UniqueModuleId` in every persistence key and filename.
- Create the module directory before writing; use atomic-replacement patterns where a partially written settings file would be harmful.
- Deserialize defensively. Catch malformed JSON, validate values and ranges, then fall back to safe defaults without deleting unrelated data.
- Never write arbitrary locations selected from module data, URLs, or user-controlled filenames.

### Secrets and privacy

- Do not store credentials, access tokens, cookies, or personal data in source control, module metadata, logs, screenshots, or issue text.
- Keep secrets in an approved host/user secret store; never encode them in XAML, JSON, shell commands, or default settings.
- Redact sensitive values in diagnostics. Prefer stable identifiers and error categories over raw payloads.

## 4. Native, process, and network boundaries

- Prefer managed, cross-platform APIs. Isolate every OS-specific API or external process in a small service that checks its operating system and availability.
- Validate executable paths and arguments. Never concatenate untrusted input into a shell command; use argument lists where available.
- Treat downloaded files and network responses as untrusted. Validate type, size, destination, and expected identity before loading or executing anything.
- Web modules must declare allowed hosts and navigation behavior through `IWebViewModule`; do not bypass the host's navigation hardening or native-view lifecycle controls.
- A missing native dependency must produce a useful disabled state or fallback UI, never a constructor crash.

## 5. Dependency policy

1. Prefer the .NET runtime or existing project dependency when it meets the need.
2. Add a package only for a concrete requirement that cannot be met safely with the existing stack.
3. Confirm Windows, macOS, and Linux support, licensing, maintenance, transitive native assets, and known advisories.
4. Pin a compatible version in the project file and verify that private runtime dependencies are deployed with the module where required.
5. Remove unused packages and never suppress a vulnerability warning without a documented risk decision and mitigation.

See [ENGINEERING_STANDARDS.md](ENGINEERING_STANDARDS.md) for preferred libraries and usage boundaries.

## 6. Security review checklist

Before merging or publishing, verify:

- [ ] Manifest ID, version, capabilities, and automatically derived tier are correct.
- [ ] No unnecessary capability or broad filesystem/network behavior was added.
- [ ] Per-instance state cannot collide or escape its module directory.
- [ ] External input, JSON, URLs, and process arguments are validated.
- [ ] Diagnostics do not reveal secrets or private data.
- [ ] Native and platform-specific behavior has a safe fallback.
- [ ] Dependencies were reviewed for desktop support and advisories.
- [ ] The changed project builds, and cross-platform checks are complete or explicitly recorded as pending.

## 7. Reporting a vulnerability

Do not publish exploit details, private keys, tokens, or customer data in a public issue. Report the issue privately to the Docklys maintainers with a minimal reproduction, affected version, impact, and safe mitigation. Preserve evidence securely and rotate any exposed credential immediately.

# 0017. Development plugins loaded from build output

**Status:** Accepted (2026-10-20). Design: [docs/design/development-plugins.md](../design/development-plugins.md).

## Context
Plugin developers need to run their plugin inside the target app from their IDE without packing and installing. Providing that harness is the app developer's job; Alcove should make it easy and safe.

## Decision (proposed)
- Alcove can load **development plugins** directly from a build output folder, into cells, with the same classification and validation as installed plugins, alongside installed plugins.
- Development plugins are never recorded in the state file, trust-verified or cleaned up, and are flagged `PluginInfo.IsDevelopment`.
- Enabling them is always the **app's** explicit choice (`options.DevelopmentPlugins` / `AddDevelopmentPluginsFrom(args, env)`).
- `Alcove.Sdk` makes build output loadable (manifest and dependencies in `bin/`) and provides harness helpers (`PluginUnderTest`, `AlcoveHarnessExecutable`). Alcove ships no app-specific harness.

## Consequences
- Fast inner loop with real isolation; differences from a real install are reported as warnings.
- A new code path that must never run in production by accident; the opt-in API and docs guard against that.

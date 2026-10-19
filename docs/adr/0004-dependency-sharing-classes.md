# 0004. Host-owned / host-shared / cell-private dependencies

**Status:** Accepted

## Context
Types shared between host and plugins (contracts, the app's SDK, the framework) must load exactly once, or type identity breaks. Other dependencies can be shared to save memory and avoid duplicates, but plugins sometimes need different versions. WPF finds XAML resources by assembly name, which makes duplicate assemblies risky.

## Decision
Each assembly is classified:
- **Host-owned:** shared frameworks, `Alcove.Abstractions`, and packages the app lists (contracts, `<TheApp>.Sdk`). Always from the default context. A version conflict makes the plugin **incompatible**.
- **Host-shared:** anything else in the host's `deps.json`. Uses the host's copy when its version satisfies the plugin's range. Otherwise it becomes cell-private (option "c": a private copy is loaded rather than rejecting the plugin).
- **Cell-private:** loaded in the plugin's cell. Covers host-shared conflicts, dependencies the host lacks, and dependencies the plugin developer explicitly marks private.

Privacy is per cell, never per plugin.

## Consequences
- Plugins rarely fail to install because of library version clashes.
- Duplicate assemblies across load contexts are possible. The library warns when a duplicated assembly contains WPF resources.
- The host's `deps.json` must be readable (affects single-file hosts; see open questions).

## Amendments
- *2026-10-17 (spike: cross-compartment-deps):* **Plugin-provided** now also covers the provider plugin's **exposed dependencies**, which resolve through its cell ([ADR 0014](0014-cross-cell-dependencies.md)).
- *2026-10-17 (spike: nuget-resolution-and-assets):* framework-provided packages are **skipped** (as the SDK prunes them), not version-checked. The host inventory comes from `APP_CONTEXT_DEPS_FILES`.
- *2026-10-17 (spike: wpf-across-load-contexts):* the WPF warning is replaced by a static detection rule based on the identity WPF actually uses (name + `AssemblyVersion` + token, WPF-relevant = has `.g.resources` or is referenced by one). A same-identity duplicate gives "will break", a different `AssemblyVersion` "may break". Identical WPF-relevant packages are shared across cells ([ADR 0015](0015-share-identical-wpf-assemblies.md), accepted 2026-10-18). App developers should list WPF control libraries in `HostOwnedPackages` where possible.
- *2026-10-19 (as built):* host-owned also covers `NuGet.Versioning`, `Microsoft.Extensions.Logging.Abstractions` ([ADR 0009](0009-separate-abstractions-package.md)) and **every dependency of a host-owned package** (e.g. `Microsoft.Extensions.DependencyInjection.Abstractions`). DI add-ons are made host-owned automatically.

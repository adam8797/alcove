# 0005. Hot install; everything else at restart; no unloading

**Status:** Accepted

## Context
Loading new code into a running process is reliable. Unloading it requires collectible load contexts and full cooperation, since any leftover reference (a static event, a cached type, a WPF binding) prevents the unload, and those failures are subtle. Windows also locks loaded DLLs.

## Decision
- **Install** takes effect immediately when it can: a new cell, or an existing cell with no version clash. Otherwise the plugin is marked *PendingRestart* with a reason.
- **Update, uninstall, enable, disable and rollback** are staged in the state file and applied at next startup, before anything loads.
- Load contexts are non-collectible. Code is never unloaded during a run.

## Consequences
- Simple, predictable runtime model. The catalog has no "removed" event.
- Apps should show a "restart to apply" message; `GetPendingChangesAsync()` supports this, including for changes made by another instance.

## Amendments
- *2026-10-17 (spike: hot-load-into-loaded-compartment):* "version clash" is defined precisely. A hot install is OK for a name the target cell has never answered, has only failed on, or would answer identically. Anything else is `PendingRestart`, listing those names (architecture §6). Cells record and freeze their answers so the rule can be checked before loading.
- *2026-10-17 (spikes: cross-compartment-deps, wpf-across-load-contexts):* also `PendingRestart`: changing a provider's exposed dependency, and creating a same-identity duplicate of a WPF-relevant assembly (which would break already-running plugins).
- A plugin Faulted for a missing dependency could start working after a later install provides it, because the runtime doesn't cache failures. *Decided 2026-10-18:* the library does **not** re-activate it during the run. It loads at next startup, and `GetPendingChangesAsync()` reports it as "will load at next startup" (consistent with [ADR 0011](0011-report-failures-never-self-heal.md)).
- *2026-10-19 (as built):* also `PendingRestart`: a plugin that registers services (`IServicePlugin`) joining a cell whose service provider is already built, and a nested install (started from plugin code) that depends on a plugin still initialising.

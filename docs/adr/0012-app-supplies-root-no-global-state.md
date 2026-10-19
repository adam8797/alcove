# 0012. App supplies the plugin root; no global state

**Status:** Accepted

## Context
Per-user, per-machine, container volume and portable installs all need different locations. Global state makes testing hard and blocks several hosts in one process.

## Decision
- `PluginHostOptions.Root` is required. The library never picks a default. (The WPF add-on may offer a helper for a conventional default.)
- No static mutable state. Several `PluginHost` instances can coexist.
- A lock file in the root serialises state changes across processes. Loading takes it only briefly (to apply staged changes and write its in-use record) and proceeds without it if another process holds it.

## Consequences
- Testable in isolation (temporary roots).
- Two app instances can share a root safely. Each sees the other's changes at restart, or through `GetPendingChangesAsync()`.

## Amendments
- *2026-10-17 (spikes):* "No global state" means the library keeps none of its own. These process-wide effects are unavoidable or opt-in, and are documented:
  - **NuGet trust store selection** (`X509TrustStore.InitializeForDotNetSdk()`), process-wide, and the first caller wins (nuget-signature-verification);
  - ~~NuGet's user-wide HTTP cache~~: *2026-10-19:* no longer an exception. Phase 4 bypasses it (`SourceCacheContext.NoCache` with a per-operation temp folder), so Alcove writes nothing outside the plugin root and temp;
  - **WPF `Default.Resolving` forwarder**, opt-in, registered per `PluginHost` and removed on dispose (wpf-across-load-contexts).
- *2026-10-19:* each running host keeps an in-use record in `<root>/.inuse/` (process ID + start time) listing the package folders it uses; startup cleanup never deletes folders a live record lists.

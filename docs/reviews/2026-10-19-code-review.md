# Code review: 2026-10-19 (after Phases 1–6)

Independent read-only review by a reviewer agent, verified by the coordinator for findings 1–2. Line numbers refer to commit `c930523` plus the in-progress Phase 8 work.

## Status

All ten findings fixed in commit `b8a4fc0` (2026-10-19), each with a regression test. Finding 5 can only be fully exercised on Windows.

## Findings

| # | Severity | Status | Area | Summary |
|---|---|---|---|---|
| 1 | High | Confirmed | `PluginHost.InstallAsync` | Deadlock when a plugin calls `host.InstallAsync` from `InitializeAsync` during a hot install: the non-reentrant `_installGate` is held across plugin activation. |
| 2 | High | Confirmed | `PluginHost.DisposeAsync` | Plugins activated after `DisposeAsync` (load or install still running) are never disposed, and lose their in-use record, so another process's cleanup may delete their files. |
| 3 | Medium | Confirmed | `HotLoadRule` / `Cell` | Native-library conflicts are missed: frozen host native answers are keyed by the requested `DllImport` name, while plans are keyed by file name. |
| 4 | Medium | Confirmed | `InstallPlanner` | Trust policy isn't re-applied to packages already on disk (kept, orphaned or shared versions) when a new plan uses them. |
| 5 | Medium | Suspected (Windows) | `AtomicFile` / `StateStore.Read` | Rename-over-target fails on Windows while another process reads `state.json` (reader lacks `FileShare.Delete`). |
| 6 | Medium/Low | Confirmed | `PluginHost` install activation | Cancellation after the state write throws `OperationCanceledException` instead of returning a result; the rest of the batch isn't marked or recorded. |
| 7 | Low/Medium | Suspected | `ExtensionRegistration`, `ExtensionCatalog` | App/plugin callbacks (`PluginFaulted`, Observe callbacks, singleton constructors) run under library locks: lock-order deadlocks possible. |
| 8 | Low | Confirmed path | `Cell` version check | A failed version check leaves the lower-version assembly loaded in the cell; a concurrent success can be reverted to Failed. |
| 9 | Low | Suspected | `CellLog` under the Gate | Logger providers run under the process-wide Gate. |
| 10 | Low | Suspected | `PackageLayout` | Package IDs from nuspec metadata are used in paths without `PackageIdValidator`. |

## Details and suggested fixes

1. **Re-entrant install deadlock.** Release `_installGate` before the activation phase (serialisation only needs resolve → write → gate-merge), or detect re-entry with an `AsyncLocal` and fail fast instead of waiting.
2. **Dispose vs. in-flight activation.** Check `_disposed` before each activation and mark the rest NotLoaded. Make `DisposeAsync` wait for an in-flight load or install (cancel via a disposal token, then wait on the gates), then dispose anything activated after the snapshot.
3. **Native keys.** When checking the hot-load rule, expand each committed requested native name with `NativeLibraryNames.Candidates` and compare with every candidate file name in the new plan, or record the candidate set with each frozen native answer.
4. **Trust for on-disk packages.** Re-verify every package a plan uses against the current policy (the `.nupkg` is kept), unless that policy's verdict is already recorded; at minimum, packages new to the affected cells.
5. **Windows sharing.** Read the state with `FileShare.ReadWrite | FileShare.Delete`, and retry the rename briefly on sharing violations.
6. **Cancellation after commit.** After the state write, convert cancellation into a result (Faulted or PendingRestart), mark the remaining plugins NotLoaded, and record pending restarts.
7. **Callbacks under locks.** Raise `PluginFaulted` after leaving the registration lock. For catalog subscription `Dispose` from another thread, mark it disposed without waiting, or document the restriction.
8. **Version check.** Check the version from metadata (`AssemblyName.GetAssemblyName`) before loading. Only revert a reservation if no concurrent request succeeded with it.
9. **Logging under the Gate.** Collect log events under the lock and emit them after releasing it.
10. **Package IDs.** Reject IDs failing `PackageIdValidator.IsValidPackageId` in the feed session and when reading state; validate plugin IDs before building data paths.

## Checked and found solid

- **Cells:** decide-and-reserve under the Gate with loads outside it, frozen answers never overwritten by failures, revert limited to its own reservation, `HotLoadRule.Check` + `MergePlan` atomic under the Gate, committed answers including planned ones, the shared registry evicting failed loads.
- **Install:** install/load ordering, PendingRestart installs leaving cells untouched, service blockers decided from metadata.
- **State:** state writes atomic under the root lock everywhere; crash during extraction is safe.
- **Cleanup:** restricted to version folders, lock-guarded, in-use records honoured.
- **Packages and trust:** zip-slip handled by NuGet's extractor; feed allow-list before download; identity double-checked; every fresh download verified before extraction; restore checks `contentHash`.
- **Activation:** fault isolation and disposal ordering.

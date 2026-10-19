# Code review, round 2: 2026-10-19 (after the round-1 fixes, commit `b8a4fc0`)

Independent read-only review focused on the round-1 fixes and the documented invariants. No High findings.

**Status:** all eleven fixed in commit `17a1d46`, each with a test (nine verified failing before the fix). Finding 7 has no dedicated test (synchronous race window).

| # | Severity | Status | Area | Summary |
|---|---|---|---|---|
| 1 | Medium | Confirmed | `ExtensionCatalog.Subscription.Deliver` | Observe callbacks run while `_delivery` is held; a second delivering thread blocks on it, so a callback that marshals to the UI thread can deadlock. Violates architecture §9. |
| 2 | Medium | Suspected | Install activation ordering | A non-nested install waits for *every* earlier activation. Plugin code that installs through an app worker (AsyncLocal not flowing) deadlocks, even for unrelated plugins. |
| 3 | Medium/Low | Confirmed | Install result status | Installs whose plugins end NotLoaded without a fault (a dependency's activation cancelled, or dispose during init) report `Faulted` and aren't recorded as pending restart. |
| 4 | Medium/Low | Confirmed | Trust for reused packages | `AllowedFeeds` can't be applied to packages already on disk (source not recorded); cached acceptances survive revocation with Online checks. |
| 5 | Low/Medium | Confirmed | `DisposeAsync` own-operation check | Fire-and-forget tasks started by plugin code inherit the operation and are treated as "own"; resources created after the dispose snapshot leak. |
| 6 | Low | Confirmed | `DisposeAsync` | A second concurrent call returns before disposal finishes. Dispose waits indefinitely for plugins ignoring cancellation. |
| 7 | Low | Confirmed | `InstallAsync` already-installed | Stale `Installed (NotLoaded)` result when the plugin finished loading in between. |
| 8 | Low | Confirmed | `LoadAsync` | Can complete normally (and run cleanup) after dispose, if dispose was called from the last plugin's init. |
| 9 | Low | Confirmed | Singleton creation | A singleton constructed while dispose runs is never disposed. |
| 10 | Low | Suspected | Init cancellation token | Plugins now get a linked token disposed when the call ends, so background work tied to it is no longer cancelled by the app's token. |
| 11 | Low | Confirmed | Install during `LoadAsync` | Plugins loaded by LoadAsync can be reported as PendingRestart, or missing from `Plugins`. |

## Suggested fixes

1. `Deliver` with `Monitor.TryEnter`; if another thread delivers, set a "more pending" flag under the catalog gate and return. The delivering thread re-checks the flag before leaving.
2. Wait only for activations the install depends on (plugin dependencies, cells it joins). Unrelated installs don't wait. Optionally add cycle detection or a timeout that turns the wait into PendingRestart.
3. Compute the status from plugin states (`Faulted` only if some plugin faulted, otherwise PendingRestart), and record every NotLoaded plugin as pending restart.
4. Record each package's source feed in the state; check it against `AllowedFeeds` on reuse (unknown source = rejected when the list is non-empty). Cache acceptances only with `Revocation = Offline`.
5. When an operation ends after disposal started, sweep and dispose runtimes and cell providers created since the snapshot, without double-disposing.
6. Store the disposal task and return it to later callers (immediately when called from within the disposal's own flow). Document that dispose waits for plugins that ignore cancellation.
7. Always recompute the already-installed result after the optional wait.
8. Check cancellation or `_disposed` after `LoadPlannedAsync`, before cleanup.
9. Dispose a singleton immediately in `SingletonCreated` if the host is disposed (or count in-flight creations).
10. Document that the init token is valid only during `InitializeAsync`, or keep the linked source until the host is disposed.
11. When LoadAsync finishes, drop pending-restart entries for plugins it loaded, and add missing PendingRestart infos to `Plugins`.

## Checked and found solid

The install gate and activation split, the nested-install rules, the DisposeAsync basics, cancellation after the state write (apart from #3), the trust verdict fingerprint, the reservation non-revert fix, deferred logging, catalog `Dispose` semantics, package-ID validation, AtomicFile sharing, cleanup under the root lock, native candidate expansion and entry-point discovery.

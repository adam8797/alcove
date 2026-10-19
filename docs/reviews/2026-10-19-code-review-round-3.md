# Code review, round 3: 2026-10-19 (the round-2 fixes, commit `17a1d46`)

Targeted read-only review of only the round-2 fix commit. Nothing above Medium.

**Status:** all six addressed (commit after `17a1d46`); #1 and #2 have tests verified failing first, #3 has clock-based unit tests, #4 and #5 can't be driven deterministically, #6 is documentation.

| # | Severity | Status | Summary | Decision |
|---|---|---|---|---|
| 1 | Medium | Confirmed | Plugin init tokens stayed linked to the app's **per-call** token for the host's lifetime: a call timeout cancelled plugin background work later, and registrations on long-lived tokens accumulated. | The plugin token follows the app's token only during the call; afterwards only host disposal cancels it. |
| 2 | Low | Confirmed | A race between an install's re-check of the load state and `FinishLoad` could list a plugin twice and record a stale pending restart. | Do the listing and pending-restart recording in one locked block, consistent with `FinishLoad`. |
| 3 | Low | Confirmed | No verdict caching with Online revocation (the default), so every install re-verifies shared packages online. | Cache Online acceptances for 10 minutes; Offline for the host's lifetime; `AllowedFeeds` always checked. |
| 4 | Low | Confirmed | A callback throwing during `Observe` could still receive a concurrent delivery before the subscription was disposed. | Dispose the subscription before releasing the delivery claim. |
| 5 | Low | Suspected | Concurrent disposal sweeps could dispose out of order. | Serialise sweeps. |
| 6 | Low | — | Plugin code handing a dependent install to another flow and awaiting it from `InitializeAsync` waits until cancellation. | Document it. |

Checked and found solid: the catalog's delivering-thread scheme (no missed or duplicate deliveries, no concurrent callbacks per subscription, re-entrancy), dependency-only install waiting (no cycles, transitive dependencies), single disposal of every resource, the second `DisposeAsync`, `LoadAsync` interrupted by dispose, and source-feed recording (the `.nupkg.metadata` fallback can't be spoofed by package content; `AllowedFeeds` is checked before the cache and fails closed).

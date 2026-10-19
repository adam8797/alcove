# 0006. Validation always runs; resolution is optional

**Status:** Accepted

## Context
Startup must be fast and must work offline. Loading a broken or tampered plugin set should still be caught.

## Decision
- **Validation** is local only and runs as part of every `LoadAsync()`. It can't be skipped. A separate `ValidateAsync()` lets apps inspect results first.
- **Resolution** (talking to feeds: restore, update checks, installs) is never implicit at startup. The app calls it when it wants to.
- The state file records the fully resolved dependency set, so loading needs no NuGet resolution.

## Consequences
- Offline startup by default; deterministic loading from `state.json`.
- Apps that want "always up to date" call `RestoreAsync()` / `CheckForUpdatesAsync()` before `LoadAsync()`.

# 0011. Report failures; never disable or roll back automatically

**Status:** Accepted

## Context
Automatic recovery (disabling a crashing plugin, rolling back a bad update) is policy, and different apps want different policies.

## Decision
- A plugin that throws during load, registration, initialisation or extension creation is marked **Faulted**, with the exception. An event is raised, and other plugins continue.
- The library never disables or rolls back by itself. It provides `DisableAsync` and `RollbackAsync` for the app to call.
- A `FailFast` option throws on the first fault (for development).

## Consequences
- Predictable behaviour; apps write their own recovery policy in a few lines.

## Amendments
- *2026-10-18:* a Faulted plugin is never re-activated during the run, even if a later hot install provides the dependency it was missing. It loads at next startup and is listed in `GetPendingChangesAsync()`.

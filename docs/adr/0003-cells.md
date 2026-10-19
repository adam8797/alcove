# 0003. Cells are the unit of isolation

**Status:** Accepted

## Context
One load context per plugin is maximum isolation, but plugins that work closely together sometimes need to share types and dependency instances. One load context for all plugins is simple, but makes conflicting dependencies impossible.

## Decision
Introduce **cells**: one cell = one non-collectible `AssemblyLoadContext` (+ one service provider when a DI add-on is used).
- Default: each plugin gets its own cell.
- Shared cells can be defined in the app's options, or requested by name in a plugin's manifest. If they disagree, the app's options win.
- A plugin can declare itself **isolated**: it must be alone in its cell. Assigning anything else to it, even by the app, fails validation.
- A cell contains exactly one version of each assembly.
- Changing any plugin in a cell reloads the whole cell at next startup.

## Consequences
- The app and plugin developers choose the trade-off between isolation and sharing.
- The name avoids "container", which already means DI container.
- *2026-10-18:* originally called "compartments"; renamed to **cells** when the project was named Alcove.
- Validation must enforce the isolation and one-version rules.

## Amendments
- *2026-10-17 (spike: hot-load-into-loaded-compartment):* "one version per cell" is enforced by the **library**, not the runtime. The runtime doesn't remember "use the host's copy" answers, so each cell freezes the first successful answer for every name (architecture §5, "Answer freezing").

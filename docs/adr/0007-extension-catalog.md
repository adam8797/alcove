# 0007. Extensions are obtained from an ExtensionCatalog

**Status:** Accepted

## Context
Hosts need to list what plugins provide and react when a hot install adds more. DI containers are fixed once built, so they can't reflect hot installs. Hosts also want to list extensions (e.g. for menus) without creating them.

## Decision
The library provides an `ExtensionCatalog`:
- `GetAll<T>()` / `GetAll<T, TMetadata>()` returns extension handles with metadata, created lazily.
- `Observe<T>(onAdded)` and an `ExtensionsAdded` event for hot installs.
- Metadata comes from registration, or from an `ExtensionAttribute` subclass instance.
- Each registration chooses its lifetime: `Singleton` (default; `Value`) or `Transient` (`CreateInstance()`).
- Events fire on the thread that performed the install. The WPF add-on marshals them to the Dispatcher.

## Consequences
- One way to get extensions, whether or not DI is used.
- DI add-ons never register extensions as `IEnumerable<T>` in the root container.

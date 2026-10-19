# 0001. Target .NET 10 and later only

**Status:** Accepted

## Context
Isolated loading depends on `AssemblyLoadContext`, which doesn't exist on .NET Framework. Supporting .NET Framework would mean a second loading implementation based on AppDomains. The first consumers are new WPF apps.

## Decision
All packages target `net10.0` (the WPF add-on targets `net10.0-windows`). No .NET Framework or .NET Standard targets.

## Consequences
- One loading model, and modern APIs (`ValueTask`, default interface methods, `required`).
- Plugins must target a framework compatible with the host's. Older plugin TFMs (e.g. `net8.0`) still load into a `net10.0` host.

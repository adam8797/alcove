# 0015. Share identical WPF-relevant private assemblies across cells

**Status:** Accepted 2026-10-18 (from spike [wpf-across-load-contexts](../../spike/wpf-across-load-contexts/REPORT.md); revisit if the Windows run contradicts the spike's predictions)

## Context
WPF resolves assemblies by name across the whole process, and the most recently loaded copy wins. With the defaults (each plugin in its own cell, and dependencies the host lacks are cell-private), two plugins using the same WPF control library each load their own identical copy. The earlier plugin's views then break. This contradicts "privacy per cell" ([ADR 0004](0004-dependency-sharing-classes.md)) for a very common case.

## Decision
When the same package ID **and** version would be cell-private in more than one cell, and the assembly is **WPF-relevant** (contains `<AssemblyName>.g.resources`, or is referenced by an assembly that does), load it **once** and return that instance from every such cell's `Load`.

Sharing applies to **WPF-relevant assemblies only**. Other identical private packages keep full per-cell isolation, including their static state. Widening it to all identical packages was considered and rejected for v1; it can be revisited later without breaking anything.

## Consequences
- Two plugins with the same control library work by default.
- Identical WPF-relevant packages are no longer fully isolated per cell, so their static state is shared.
- Same-identity duplicates that are *not* the same package (same `AssemblyVersion`, different content) still conflict and are reported as "will break".

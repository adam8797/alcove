# 0014. Cross-cell plugin dependencies via exposed dependencies

**Status:** Accepted 2026-10-18 (backed by spike [cross-compartment-deps](../../spike/cross-compartment-deps/REPORT.md))

## Context
Plugin B in cell Y can depend on plugin A in cell X. The spike showed:
- Y's `Load` returning `X.LoadFromAssemblyName(...)` gives full type agreement.
- When A's public API exposes types from a dependency D, B **must** get D from X. A separate copy breaks, sometimes silently (`is` returns false, plus a type-safety hole).
- The runtime doesn't check the version a custom `Load` returns.
- Cells must map the **transitive** plugin-dependency closure.

## Decision
- Cross-cell plugin dependencies are allowed.
- At install, each plugin's **exposed dependencies** are found by scanning its public API with `System.Reflection.Metadata`, and stored in the state file.
- Every cell depending on a plugin resolves that plugin **and its exposed dependencies** through the plugin's cell.
- If a consumer needs a version of an exposed dependency that the provider's cell doesn't satisfy, validation reports `ExposedDependencyConflict`. A duplicate is never loaded.
- A plugin is loaded exactly once in the process.
- Returned versions are checked against the requested version.
- Dependencies that aren't exposed stay independent per cell.

## Consequences
- Plugin authors whose plugins are depended on should avoid exposing third-party types in their public API (plugin developer guide).
- A hot install that would change a provider's exposed dependency is `PendingRestart`.
- From the host, assembly-qualified `Type.GetType` needs `EnterContextualReflection`.

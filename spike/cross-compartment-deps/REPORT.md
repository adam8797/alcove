# Spike report: cross-compartment plugin dependencies

> Written by the orchestrator from the spike agent's findings (the agent's environment didn't allow it to write this file). Raw evidence: [`results.txt`](results.txt), the output of the last full run (19 host scenarios plus the leak scanner). Lines marked `[FAIL]` under an `expectation: ... FAIL` line are failures the scenario was set up to show.

## Summary

Cross-compartment plugin dependencies **work**, with one rule: **a plugin's exposed dependencies resolve through that plugin's compartment.**

- Y's `Load` returning `X.LoadFromAssemblyName(name)` gives full type agreement between B and A (casts, interfaces both ways, generics, exceptions, subclassing, reflection).
- If A's public API exposes types from a compartment-private dependency (Dep), and B gets its *own* copy of Dep (even the identical file), things break: `MissingMethodException` at JIT, `InvalidCastException` at call time, and two silent failures (`is Widget` returns false, plus a type-safety hole).
- If Y delegates Dep to X, all of that works. But **the runtime doesn't check the version a custom `Load` returns**, so version ranges must be enforced by validation.
- Leaks can be found statically with `System.Reflection.Metadata` in 4–36 ms per assembly.
- Chains and diamonds work if each compartment maps the **transitive closure** of its plugin dependencies.

## Answers

### Q1. Basic cross-compartment use (Q1.1–Q1.3)
- Y's `Load(A)` returning `X.LoadFromAssemblyName(A)` works: casts, interfaces in both directions, `Box<BThing>`, exceptions, subclassing and reflection all agree on A's types. A is loaded into X on demand.
- Returning only assemblies X has *already* loaded is order-dependent and fails with `FileNotFoundException` when B loads first (Q1.3). So delegate with `LoadFromAssemblyName`, don't look things up.

### Q2. A exposes a type from Dep, which is private in X
- **2a.** B doesn't reference Dep: works. B can't call A members that have Dep in their signature without referencing Dep.
- **2b.** B gets its own copy of Dep (Q2b.1–Q2b.3), even the identical 2.0 file:
  - calls to members with Dep in the signature fail with `MissingMethodException` when the calling method is JIT-compiled;
  - casts fail with `InvalidCastException` at call time;
  - **silent failures:** `is Widget` returns false, and a type-safety hole lets Y's `Widget` code run on an object whose base is X's `Widget`.
- **2c.** Y delegates Dep to X (Q2c.1–Q2c.6): fixes everything.
  - **The runtime doesn't check the version a custom `Load` returns.** 2.0 is accepted for a 3.0 request, and B's v3-only call then fails with `MissingMethodException` (Q2c.2). Validation has to check version ranges.
  - Dependencies A doesn't expose can stay separate per compartment (Q2c.3–Q2c.4).

### Q3. Static detection
- A scanner using only `System.Reflection.Metadata` finds every planted leak in A's public surface and none of A's internal uses. It costs 4–36 ms per assembly, including large ones (CoreLib, Roslyn).
- On real packages it found, for example, NuGet.Protocol exposing Newtonsoft.Json types.
- Scanning B's call sites misses the cast and the type-safety hole, so consumer scanning is only useful for diagnostics. **Rely on scanning the provider (A), not the consumer (B).**

### Q4. Chains and diamonds (Q4.1–Q4.5)
- Both work.
- Each compartment must map the **transitive closure** of its plugin dependencies. Mapping only direct dependencies gives `FileNotFoundException` (Q4.3).
- A duplicate copy of A (Q4.4) breaks B's API for C. A plugin must only ever be loaded once.

### Q5. Reflection
Works from plugin code. From the host, assembly-qualified type names (`Type.GetType("...")`) need `AssemblyLoadContext.EnterContextualReflection`.

## Recommendation

**Allow cross-compartment dependencies**, with this rule:

> A plugin's **exposed dependencies** (dependencies whose types appear in its public API, found by the scanner at install time and stored in the state file) resolve through the plugin's compartment for every compartment that depends on it. If a consumer needs a version that the provider's compartment doesn't satisfy, validation reports `ExposedDependencyConflict`. A silent duplicate is never loaded.

### `Load` resolution order

1. **Host-owned** (including shared frameworks): return `null`.
2. A plugin in the compartment's **transitive** plugin-dependency closure, or one of that plugin's **exposed dependencies**: `X.LoadFromAssemblyName(name)`.
3. **Compartment-private**, including the compartment's own plugin assemblies: load from path.
4. **Host-shared**: return `null`.
5. **Unknown**: return `null`, log at debug level.

After steps 2 and 3, check the returned version against the requested one. The hot-load spike independently found the same version problem and recommends rejecting lower versions outright rather than only warning.

## Impact on the plan and docs

- **architecture.md** §4/§5: extend the plugin-provided row to cover exposed dependencies. Replace the resolution order with the one above. Add the exposed-dependency validation rule. Store exposed assemblies in the state file and compute them at install.
- **implementation-plan.md** Phase 3: add the public-surface scanner, the `ExposedDependencyConflict` validation rule, and these scenarios as integration tests. Extend "Done when". Update the cross-compartment risk row.
- **ADRs:** new ADR 0014 (cross-compartment dependencies and exposed dependencies). Amend 0004 (new classification) and 0003.
- **open-questions.md:** close spike questions 1–2. The WPF part (3) stays with the WPF spike.
- **CONTEXT.md:** add "Exposed dependency".
- **plugin-developer-guide.md:** advise authors of plugins that others depend on not to expose third-party types in their public API.
- **Interaction with hot-load rule:** a hot install that would require changing X's exposed dependency is a PendingRestart case.

## How to re-run

```
cd spike/cross-compartment-deps
./build.sh && ./run.sh > results.txt
```

Needs SDK 10.0.203 (pinned in `global.json`).

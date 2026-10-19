# Spike: hot install into an already-loaded compartment

## Goal
Define the exact rule for when a newly installed plugin can load immediately into a compartment that's already running, versus being marked `PendingRestart` (open question "Hot install into a running shared compartment: exact rule").

## Background
Compartments are non-collectible `AssemblyLoadContext`s. Once a context has loaded an assembly name, it can't load a different version of that name. The runtime also caches the result of resolution attempts, including `Load` returning `null` (falling back to the default context).

## Questions
1. After a compartment has loaded `D 1.0`, what happens when asked to load `D 2.0` (`LoadFromAssemblyPath`, and through the `Load` override)? Exception type, message, and whether the compartment stays usable.
2. If the compartment's `Load(D)` previously returned `null` (so D resolved from the default/host context), and a newly installed plugin now wants a compartment-private D: can the compartment change its answer? Which references are already bound and can't change?
3. If an earlier `Load(D)` failed (D unknown, so `FileNotFoundException`), and D later becomes available: is the failure cached? Per context? Can the context recover?
4. Adding entirely new assemblies to a loaded compartment: any restrictions?
5. Does the timing matter: resolved but never JIT-compiled vs. actually used?
6. Native libraries (`LoadUnmanagedDll`): the same questions for a native library already loaded in the process.

## Experiments
A console host with a `Compartment : AssemblyLoadContext` whose mapping table can be changed at runtime (simulating a hot install), and one scenario per question printing PASS/FAIL. Version pairs of a small class library D (1.0, 2.0) with different strong/simple versions. Native library question: use any small native library available on Linux (or skip with explanation if impractical).

## Success criteria
- A precise rule the install pipeline can check *before* attempting a hot load, phrased in terms of state the library knows: (the compartment's current resolution table) × (the new plugin's resolved assembly set) → hot load OK / PendingRestart with a reason.
- What the compartment must record at runtime to evaluate the rule (e.g. "every name ever requested, and the answer given").

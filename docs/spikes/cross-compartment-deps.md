# Spike: cross-compartment plugin dependencies

## Goal
Decide whether plugin B in compartment Y can depend on plugin A in compartment X (architecture §5, resolution step 2), and under what rules.

## Background
Each compartment is a non-collectible `AssemblyLoadContext`. B's compartment would answer `Load(A)` by returning the `Assembly` instance already loaded in X. Type identity is per loaded assembly instance, so B and A should agree on A's types. The risk is **leaked dependencies**: A's public API exposes a type from a dependency that is compartment-private in X.

## Questions
1. Does returning X's `Assembly` from Y's `Load` override work? Can B implement A's interfaces, pass objects to A, cast A's objects, use A's generic types?
2. A's public API exposes a type from dependency D, where D is compartment-private in X (e.g. `X` has D v2 while the host has D v1):
   a. B doesn't reference D at all: does it work?
   b. B references D and gets it from Y: what fails, how (exception type and message), and when (load, JIT, call)?
   c. If Y resolves D by delegating to X ("B uses A's copy of A's dependencies"), does that work? What if B also needs a *different* version of D for itself?
3. Can we detect leaked dependencies **statically** at install/validate time (e.g. with `System.Reflection.Metadata`, inspecting A's public surface for types from its dependencies)? How expensive is it?
4. Chains and diamonds: C(Z) → B(Y) → A(X); and C(Z) → A(X) plus B(Y) → A(X). Anything surprising?
5. Does `AssemblyLoadContext.GetLoadContext`, `Assembly.GetType`, `Type.GetType("...")` with assembly-qualified names, and `Activator.CreateInstance` behave as expected across compartments?

## Experiments
- A console host with a contracts assembly in the default context.
- Plugin A (with dependency D v2), plugin B, plugin C; dependency D in v1 (host) and v2 (private). Plain class libraries are enough; packages aren't required.
- A minimal `Compartment : AssemblyLoadContext` implementing the resolution order from architecture §5, with a mapping table to configure "assembly name → which context provides it".
- One scenario per question, each printing PASS/FAIL with details.

## Success criteria
A clear recommendation, one of:
- cross-compartment dependencies allowed in general;
- allowed with a specific rule (e.g. "dependencies exposed in A's public API resolve through X"), with a sketch of how validation detects violations;
- not allowed: plugin dependencies must share a compartment.

Plus the exact `Load` resolution order the core should implement.

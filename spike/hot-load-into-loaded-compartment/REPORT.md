# Spike report: hot install into an already-loaded compartment

> Written by the orchestrator from the spike agent's findings (the agent's environment didn't allow it to write this file). Raw evidence: [`results/all.txt`](results/all.txt) — 30 checks, 30 PASS, 0 FAIL, .NET runtime 10.0.7 on Linux.

## Summary

- A compartment can't hold two versions of one assembly name; the runtime throws `FileLoadException` and the compartment stays usable. **Surprise:** if `Load` returns an already-loaded *lower* version, the runtime accepts it and the failure surfaces later as `MissingMethodException`.
- **The real danger:** the runtime does **not** remember a "use the host's copy" (`Load` returned `null`) answer per compartment. Each newly loaded assembly asks again. If the answer changes, one compartment ends up with two copies of D (A sees host D 1.0, B sees private D 2.0). Type identity splits silently, with no error.
- Failed resolutions are never cached; a missing dependency that appears later is picked up, even by the method that failed.
- Adding assemblies with new names to a loaded compartment has no restrictions.
- Binding happens on first JIT of a method mentioning the type (even in an untaken branch), on type load, `GetTypes()`, or reading parameter types. Not on assembly load, `GetReferencedAssemblies()` or `GetMethods()`. A name nobody has asked for yet can still be switched safely.
- Native libraries: each P/Invoke binds once, and remaps can produce two copies. Two copies loaded by full path coexist. But a native library's *own* dependencies are matched by soname against everything already loaded anywhere in the process, so the v2 library got v1 of its dependency. A restart doesn't fix it. Not tested on Windows.
- **Conclusion:** the library, not the runtime, must enforce "one answer per name per compartment". With that, a precise hot-load rule is possible (below).

## Answers

### Q1. Loading a second version of D into a compartment
- `LoadFromAssemblyPath(D 2.0)` after D 1.0 is loaded throws `FileLoadException` ("Assembly with same name is already loaded").
- Through the `Load` override, it throws `FileLoadException` with HRESULT 0x80131053.
- The compartment stays usable afterwards, and the failure isn't cached.
- A request for D 1.0 when D 2.0 is already loaded is satisfied without calling `Load`.
- If `Load` returns an already-loaded **lower** version than requested, it's accepted. The failure comes later as `MissingMethodException`. So the core must check versions itself.
- Same name and version with different contents throws; a byte-identical copy returns the existing instance.

### Q2. Host's D first, then a compartment-private D
- Each plugin assembly's reference to D binds once, permanently, including for methods not yet compiled.
- But the compartment doesn't cache a `null` answer. So: plugin A loads, `Load(D)` returns `null` → host D 1.0. Hot install of B changes the table to private D 2.0 → B gets D 2.0. **One compartment now has two Ds.** A's object isn't B's `BaseThing`, and no error is raised.
- The other direction (private first, then host) is pinned by the runtime, because the private D is now loaded in the compartment.

### Q3. Failure, then the dependency becomes available
Not cached anywhere: not when `Load` returns `null` or throws, and not for the `Resolving` event. Recovery works, including for the method that failed earlier.

### Q4. New assemblies in a loaded compartment
No restrictions on new names. A private copy alongside a different host version is fine.

### Q5. Does timing matter?
Yes. Binding happens on first JIT of a method mentioning D (even in an untaken branch), on type load, `GetTypes()`, or reading parameter types. It doesn't happen on assembly load, `GetReferencedAssemblies()` or `GetMethods()`. Until a name has been requested, its answer can still be changed safely.

### Q6. Native libraries
- Each P/Invoke binds once. A newly loaded assembly calls `LoadUnmanagedDll` again, so a remap gives two copies. Failures aren't cached.
- Two copies of a library loaded by full path work side by side.
- **Caveat:** the native library's own dependencies resolve by soname against whatever is already loaded anywhere in the process. In the test, the v2 library got v1 of its dependency. A restart doesn't fix this. Windows behaviour is unverified (Windows matches already-loaded DLLs by module name, so it's likely similar).

## The rule

### What each compartment records

- **Plan:** the planned answer for every assembly in the resolved dependency sets of its loaded plugins (source: host / compartment-private path / other compartment, and package version).
- **Given:** the first *successful* answer `Load` actually handed out for any name, including names outside the plan, plus native library paths. Failures are recorded via the `Resolving` event but never overwrite a success.
- **Committed** = Plan ∪ Given.

### What `Load` must do

- **Freeze answers:** once a name has a successful answer, keep returning it. Use one lock shared with the install pipeline.
- **Check versions:** reject a returned assembly older than the requested version, instead of letting a `MissingMethodException` appear later.

### At install time

Re-resolve the target compartment *together with* the new plugin. For every name in the new resolution:

- **Hot-load OK** if the compartment hasn't committed it, or it only failed before, or the new answer is identical (same source and same package version; for native libraries, same path).
- Otherwise **PendingRestart**, with the list of conflicting names as the reason.
- A plugin going into a **new** compartment always hot-loads.

The `rule-demo` scenario in `src/Host/Rule.cs` confirms these verdicts, and shows that with frozen answers, bypassing the rule fails loudly instead of silently splitting D.

## Impact on the plan and docs

- **architecture.md**
  - §5: add answer freezing, the version check, and recording of all answers including "unknown" and native ones.
  - §6 Install: replace "no version clash" with the rule above.
  - §4: add a warning about native library soname collisions.
- **ADR 0005:** define "clash" by reference to this rule. Add as a consequence that a Faulted plugin may start working after a later install provides its missing dependency.
- **ADR 0003:** the library enforces one version per compartment for names answered from the host's context, not the runtime.
- **open-questions.md**
  - Move the hot-install rule to Resolved.
  - New decision: should the library re-run activation of a Faulted plugin when a later install provides its missing dependency?
  - New known limitation: native libraries with the same soname in several compartments.
- **implementation-plan.md**
  - Phase 3: the compartment records and freezes answers, checks versions, uses one lock, and warns on soname collisions. Port scenarios `q1-return-existing-lower`, `q2-split-identity`, `q3-failure-then-available` and `q6-native-remap` as tests.
  - Phase 4: `InstallAsync` applies the rule using joint re-resolution. Add a done-when case: a plugin that needs a private version of a name the compartment already took from the host gets PendingRestart with that reason.
  - Phase 5: `GetPendingChangesAsync` includes PendingRestart installs with their reasons.
- **CONTEXT.md:** add "Pending restart".

## How to re-run

```
cd spike/hot-load-into-loaded-compartment
./build.sh && ./run.sh
grep -E '^===|\[FAIL\]|RESULT' results/all.txt
```

Needs gcc (for the native test libraries) and SDK 10.0.203 (pinned in `global.json`).

## Windows results (2026-10-21)

Windows 11 Pro 10.0.26200 (x64), .NET SDK 10.0.402, MSVC 14.51 (Visual Studio 2026). Only the native scenarios (`q6-*`), from a copy of the spike outside the repository, with two changes there: `native/nat.c` and `native/natuser.c` built with `cl /LD` instead of gcc (same file names, `libnat.so` and `libnatuser.so`, so `Program.cs` needs no changes; `libnatuser.so` imports `libnat.so` by module name, like `DT_NEEDED`), and `Program.cs`'s `path.Split('/')` (used only to print folder names) also splitting on `\`.

| Scenario | Result |
|---|---|
| `q6-native-remap` | As on Linux: a P/Invoke binds once (`OK: 1` after the remap, no new `LoadUnmanagedDll` call); a new assembly in the same context asks again and gets v2 |
| `q6-native-already-in-process` | As on Linux. v2 loaded by full path works next to the v1 the host loaded (`OK: 2`). **`libnatuser.so` from the v2 folder sees `nat_version` → 1**: Windows resolved its import to the already-loaded module with the same name, not the `libnat.so` next to it |
| `q6-native-soname-control` | As on Linux: with nothing loaded first, `libnatuser.so` gets the `libnat.so` next to it (`OK: 2`) |
| `q6-native-failure-then-available` | As on Linux: a failure isn't cached |

All checks pass, so the caveat holds on Windows too, by module name instead of soname. Alcove's integration tests now build their native test library with MSVC on Windows (found with vswhere) and run the native cell scenarios there.

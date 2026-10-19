# Spike report: WPF across load contexts

> Saved by the orchestrator from the spike agent's returned text (the agent's environment didn't allow it to write report files). Content is the agent's.

Spike description: [docs/spikes/wpf-across-load-contexts.md](../../docs/spikes/wpf-across-load-contexts.md). Terms are as in [CONTEXT.md](../../CONTEXT.md).

> **Status:** built and partly verified on Linux. **Not yet run on Windows.** Anything that needs a running WPF is a **prediction** from the WPF source (dotnet/wpf `release/10.0`, commit `b784c5bd`), with a confidence level and the source it's based on. The Windows run (checklist in §5) checks every prediction automatically and reports `matches prediction` or `!! UNEXPECTED`.

## 1. Summary

- **A single copy of each assembly works (predicted, high confidence).** WPF has no notion of `AssemblyLoadContext`: the term appears nowhere in the dotnet/wpf source. It finds assemblies by scanning `AppDomain.CurrentDomain.GetAssemblies()`, which lists assemblies from **every** load context (verified on Linux). As long as each assembly name is loaded once in the process, a plugin in a compartment can use all of these:
  - compiled BAML (`InitializeComponent`);
  - `pack://application:,,,/Plugin;component/...` and `Application.LoadComponent`;
  - merging dictionaries into `Application.Resources`;
  - `xmlns` references to its own, host-owned and other plugins' assemblies;
  - implicit `DataTemplate`s and `DynamicResource` to host keys (Q1–Q4, Q6).
- **Duplicates are worse than the architecture's warning says (predicted, high confidence).** WPF looks assemblies up by *name*, narrowed by *AssemblyVersion*/token only if the request names them, and **the most recently loaded match wins**. WPF also re-points its pack-URI cache to the newest copy whenever another assembly with the same name loads (`AppDomain.AssemblyLoad`). The consequences:
  - **Identical copies break.** For example, the same NuGet package being compartment-private in two compartments. Once the second copy loads, `UserControl`s of the *earlier* copy throw `The component 'X' does not have a resource identified by the URI '...'`, and its implicit `DataTemplate`s stop matching (scenarios 7, 8). Under the current defaults (each plugin in its own compartment, and dependencies the host lacks are compartment-private), **two plugins that use the same WPF control library hit this by default.**
  - **The identity that counts is `AssemblyVersion`, not the NuGet version.** Libraries often keep `AssemblyVersion` fixed (e.g. `2.0.0.0` for all 2.x releases), so two NuGet versions can count as "identical".
  - **Different `AssemblyVersion`s mostly work**, but anything resolved by name only gets the newest copy (scenario 5):
    - `pack://` URIs without a version;
    - cross-assembly `ResourceDictionary.Source` values in XAML;
    - loose XAML;
    - `InitializeComponent` of any assembly built **without an explicit `AssemblyVersion` MSBuild property**. That's the SDK default; verified on Linux that the generated URI then has no `;V…;` part.
  - **A hot install can break plugins that are already running.** Loading a new duplicate retargets lookups for views created *afterwards*.
- **New problem: WPF never asks the compartment for an assembly.** WPF's fallback is `Assembly.Load` called from PresentationFramework, so it runs in the **default** context. So these fail:
  - a compartment-private assembly that's used **only from XAML** and not loaded yet (scenario 9);
  - a private dependency **rolled forward** to a higher `AssemblyVersion` than the plugin was compiled against (scenario 10).

  Mitigations, each verified at the runtime level on Linux (the WPF end-to-end effect is predicted):
  - pre-loading compartment-private assemblies;
  - `EnterContextualReflection`;
  - a `Default.Resolving` forwarder.
- **Recommendations** (§6):
  - The WPF add-on pre-loads compartment-private assemblies and installs a `Default.Resolving` forwarder.
  - The core loads identical XAML-bearing assemblies **once** and shares them across compartments, instead of only warning.
  - The duplicate warning is computed statically from metadata, using the identity WPF actually uses: name + `AssemblyVersion` + token, plus "has `<Name>.g.resources`".
  - The plugin SDK sets `AssemblyVersion` explicitly so generated URIs are versioned.
  - The host enables `Switch.System.Windows.Baml2006.AppendLocalAssemblyVersionForSourceUri`.

## 2. What was built

    spike/wpf-across-load-contexts/
    ├── Directory.Build.props      net10.0-windows, UseWPF, EnableWindowsTargeting (builds on Linux)
    ├── run-windows.ps1            build + run all scenarios + zip results (Windows)
    ├── src/
    │   ├── HostSdk/               host-owned: ISpikePlugin, SharedBanner UserControl, HostKeys (ComponentResourceKey), Themes/HostStyles.xaml
    │   ├── SharedControls.Common/ shared code: VersionBadge.xaml.cs, UnversionedBadge.xaml.cs, FancyBox (lookless), ThemeInfo
    │   ├── SharedControls.v1/     AssemblyName=SharedControls, AssemblyVersion 1.0.0.0, red    "v1"
    │   ├── SharedControls.v1b/    AssemblyName=SharedControls, AssemblyVersion 1.0.0.0, orange "v1b" (same identity, different content)
    │   ├── SharedControls.v2/     AssemblyName=SharedControls, AssemblyVersion 2.0.0.0, green  "v2"
    │   ├── PluginA/               view uses local:, host:, shared: (v1); Themes/Plugin.xaml (implicit DataTemplate, merges SharedControls dict unversioned)
    │   ├── PluginB/               SharedControls v2; uses PluginA's type and dictionary (plugin dependency, A's compartment)
    │   ├── PluginC/               SharedControls v1b
    │   ├── PluginD/               SharedControls v1 used ONLY from XAML (no x:Name, no code reference)
    │   └── WpfHost/               minimal compartment loader (architecture §5 order) + scripted scenarios
    ├── tools/
    │   ├── XamlAsmInspect/        net10.0, runs on Linux: identity, .g.resources, BAML parts, assembly names recorded in BAML
    │   ├── AlcProbe/              net10.0, runs on Linux: runtime half of the predictions (load order, WPF lookup algorithm, Assembly.Load routing)
    │   └── UriCodegenCheck/       what URI InitializeComponent gets with/without an explicit AssemblyVersion
    └── evidence/                  Linux outputs of the three tools + generated URIs

The host build copies plugin outputs into `bin/.../plugins/<Name>/`:
- `PluginA` gets SharedControls v1, `PluginB` v2, `PluginC` v1b, `PluginD` v1.
- `PluginA-rollforward` gets PluginA with SharedControls **v2**.

HostSdk is the only non-framework assembly in the host's `deps.json`. The build fails if SharedControls or a plugin leaks into the host folder.

**Scripted mode.** Each scenario runs in a **fresh child process**, because WPF's caches and the AppDomain assembly list are process-wide. Each scenario:
- renders off-screen and saves PNGs to `results/`;
- prints one block per check: `PASS`/`FAIL` (did WPF behave correctly from the plugin's point of view), the predicted outcome, `matches prediction` or `!! UNEXPECTED`, what was observed (rendered markers, types, versions and ALC names, exception chain), and the prediction's reasoning;
- logs every compartment `Load()` decision.

**Exit code** of `--all`: 0 when every check matched its prediction.

## 3. Verified vs predicted

| Claim | Status | Evidence |
|---|---|---|
| Everything compiles on Linux, including XAML → BAML (`MarkupCompilePass1/2`, `.g.resources`) | **Verified** (re-confirmed by the orchestrator: 0 errors) | `dotnet build src/WpfHost/WpfHost.csproj` |
| BAML records referenced assemblies by **full name incl. AssemblyVersion** (`SharedControls, Version=1.0.0.0, ...` in A, `2.0.0.0` in B) | **Verified** | `evidence/linux-baml-inspection.txt` |
| Cross-assembly `ResourceDictionary.Source` is stored in BAML as an unversioned string (`/SharedControls;component/Themes/Shared.xaml`) | **Verified** | same |
| `InitializeComponent` URI has `;V<ver>;` **only if `AssemblyVersion` is an explicit MSBuild property**; `<Version>` alone gives `/Name;component/...` | **Verified** | `evidence/linux-uri-codegen.txt`, `evidence/linux-generated-uris.txt` |
| `AppDomain.GetAssemblies()` lists assemblies from all ALCs, in load order; WPF's lookup algorithm (copied verbatim) returns the **last loaded** match | **Verified** (runtime level) | `evidence/linux-alc-probe.txt` [3] |
| `Assembly.Load` from a default-context caller (PresentationFramework's situation) can't see compartment-private assemblies | **Verified** (runtime level) | AlcProbe [1] |
| `EnterContextualReflection(compartment)` routes that load into the compartment | **Verified** (runtime level) | AlcProbe [2] |
| A custom ALC may satisfy a 1.0.0.0 request with 2.0.0.0; a `Default.Resolving` handler may return another ALC's 2.0.0.0 for a 1.0.0.0 request | **Verified** (runtime level) | AlcProbe [4], [5] |
| All WPF rendering/resolution outcomes in §4 | **Predicted** | WPF source, §4/§8; checked by the Windows run |

## 4. Answers per question

Source paths are relative to `src/Microsoft.DotNet.Wpf/src/` in dotnet/wpf `release/10.0`.

### Q1. Can the host create and show a compartment plugin's XAML `UserControl` (BAML via `InitializeComponent`)?

**Yes. Predicted, high confidence** (scenario 1).

`InitializeComponent` → `Application.LoadComponent(this, "/PluginA;V1.0.0.0;component/pluginaview.xaml")` (`PresentationFramework/System/Windows/Application.cs`, `LoadComponent(Object, Uri)`). The stream comes from `ResourceContainer.GetResourceManagerWrapper` (`PresentationFramework/MS/Internal/AppModel/ResourceContainer.cs`) → `BaseUriHelper.GetLoadedAssembly` (`PresentationCore/System/Windows/Navigation/BaseUriHelper.cs`) → `SafeSecurityHelper.GetLoadedAssembly` (`Shared/MS/Internal/SafeSecurityHelper.cs`). That scans `AppDomain.CurrentDomain.GetAssemblies()`, which includes compartment assemblies. With one PluginA in the process, the right assembly is found, and the check `bamlStream.Assembly != component.GetType().Assembly` in `LoadComponent` passes.

### Q2. `pack://application:,,,/PluginA;component/Themes/Plugin.xaml`: load by URI, merge into `Application.Resources`, `Application.LoadComponent`?

**Yes, all three. Predicted, high confidence** (scenario 2: absolute and relative URIs, versioned `;v1.0.0.0;` form, merge + `TryFindResource`, `LoadComponent(Uri)`).

It's the same `ResourceContainer` path as Q1. `ResourceDictionary.Source` (`PresentationFramework/System/Windows/ResourceDictionary.cs`) → `WpfWebRequestHelper` → `ResourceContainer`. A relative `/Asm;component/...` URI without a base resolves against `pack://application:,,,/` (`BindUriHelper.GetResolvedUri`).

### Q3. XAML referencing types in its own assembly, a host-owned assembly, and another plugin's assembly

**All three work. Predicted, high confidence** (scenario 3).

BAML assembly records hold full names (verified). `Baml2006SchemaContext.ResolveAssembly` (`PresentationFramework/System/Windows/Markup/Baml2006/Baml2006SchemaContext.cs`) uses `SafeSecurityHelper.GetLoadedAssembly`, which is AppDomain-wide:
- own assembly → the compartment's copy;
- `HostSdk` → the default-context copy (the same `Type` the host uses);
- `PluginA` from B's XAML → A's assembly in compartment A (the same `Type` B's compartment returns).

WPF bypasses ALC isolation here. That works in our favour, but only because the dependency is already loaded (see Q3+).

**Q3+ (new finding, scenario 9). A compartment-private assembly referenced only from XAML fails if nothing has loaded it yet. Predicted, high confidence.**

When `GetLoadedAssembly` finds nothing, `Baml2006SchemaContext.ResolveAssembly` falls back to `Assembly.Load(fullName)`, then `Assembly.Load(shortName)`. Those calls come from PresentationFramework, so they bind in the **default** context. It has no copy, so the result is a `FileNotFoundException` inside a `XamlParseException`. The runtime behaviour was verified on Linux (AlcProbe [1]).

Plugin code only triggers the load through the compartment if it touches the type, through an `x:Name` field cast in `Connect` or through code. So "uses a control library only in XAML" is fragile.

Mitigations: 9b `EnterContextualReflection`, 9c pre-loading, 9d `Default.Resolving` forwarder. The runtime half of each was verified (AlcProbe [2], [5]); the WPF end-to-end result is predicted to PASS. `EnterContextualReflection` only covers code inside its scope, not later template expansion, so **pre-loading is the robust fix**.

### Q4. Implicit `DataTemplate`s (`DataType="{x:Type vm:...}"`) from a plugin dictionary

**Yes, when the plugin assembly exists once. Predicted, high confidence** (scenario 4: merged into app resources and into a local `Resources`). `DataType` resolves through the same BAML assembly lookup, `DataTemplateKey` matches on `Type` identity, and the plugin creates its view-model from the same assembly.

**Breaks with a same-identity duplicate (scenario 8.3, high confidence).** The pack URI loads the other copy's dictionary, so `DataType` is the other copy's type, and the plugin's view-model gets no template (its `ToString()` is shown instead).

### Q5. Duplicate names: v1 and v2 of a XAML library in two compartments

The result depends on whether `AssemblyVersion` differs, not on the NuGet version.

**Different `AssemblyVersion` (1.0.0.0 vs 2.0.0.0, scenario 5):**

| Lookup | Result | Confidence |
|---|---|---|
| Types referenced from BAML (`shared:VersionBadge` in A's view → v1, in B's → v2) | Correct (version is in the BAML record) | High |
| `InitializeComponent` with a versioned URI (explicit `AssemblyVersion` property) | Correct | High |
| Lookless control templates from `Themes/Generic.xaml` (`SystemResources.LoadDictionary` reads `<Name>.g.resources` from the control's actual `Assembly` object; `_dictionaries` is keyed by `Assembly`) | Correct | High |
| Explicit `;v1.0.0.0;` / `;v2.0.0.0;` pack URIs | Correct | High |
| `InitializeComponent` with an **unversioned** URI (SDK default when only `<Version>` is set; simulated by `UnversionedBadge`) | Works until the other copy loads (5.0); after that the **earlier copy throws** "does not have a resource identified by the URI" (5.7) | High |
| Cross-assembly `ResourceDictionary Source="/SharedControls;component/..."` inside A's XAML | Gets **v2's** dictionary (5.8) | High |
| Loose XAML (`XamlReader.Parse`, `assembly=SharedControls`) | Most recently loaded copy (5.10) | Medium-high (see the `ReflectionHelper` caveat in Q7) |

**With vs without a version in the pack URI:** with `;v2.0.0.0;`, the lookup and the cache key include the version (`BaseUriHelper.GetAssemblyNameAndPart`, `ResourceContainer` key `name+version+token`), so it's correct. Without a version you get the newest copy.

**Same `AssemblyVersion`, different content (v1 vs v1b, scenario 7), all after C loads:**
- **7.2.** A's view: its BAML asks for `SharedControls, Version=1.0.0.0`; the last match is C's copy, so a v1b `VersionBadge` is created, and A's generated `Connect` cast `(SharedControls.VersionBadge)target` throws `InvalidCastException`. High confidence.
- **7.3.** A `VersionBadge` created by A's code: "does not have a resource identified by the URI". High confidence.
- **7.4.** A's `FancyBox`: v1's `generic.baml` is read from the right assembly, but its `{x:Type local:FancyBox}` resolves to C's type, so A's control gets no default style and renders empty. Medium confidence.

**Identical copies (scenario 8: the same PluginA in compartments A1 and A2):** A1's views fail as soon as A2 loads (8.2), and A1's view-models lose their templates (8.3). High confidence.

**Why "the newest copy" even for lookups cached earlier:** `ResourceContainer.GetPartCore` attaches `AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoadEventHandler`. That handler calls `UpdateCachedRMW`, which repoints every cached wrapper whose key (`name`, `name+version`, `name+token`, `name+version+token`) matches the newly loaded assembly. (The source comment says it exists for the Blend designer.) So **a hot install that loads a duplicate changes behaviour for views that already-running plugins create afterwards.**

### Q6. `DynamicResource` to app-level resources defined in `HostSdk`

**Yes. Predicted, high confidence** (scenario 6: typed `ComponentResourceKey` from `HostKeys`, a string key, and live update after replacing the app resource). It's a key-equality lookup up the tree to `Application.Resources`. `HostSdk` is host-owned, so `ComponentResourceKey.TypeInTargetAssembly` is the same `Type` everywhere.

### Q7. How WPF maps an assembly name to an `Assembly`

1. **`SafeSecurityHelper.GetLoadedAssembly(AssemblyName)`** (`Shared/MS/Internal/SafeSecurityHelper.cs`). Iterates `AppDomain.CurrentDomain.GetAssemblies()` **from the end**. It compares the name case-insensitively, and version, culture and token only *if the request specifies them* (version must be equal, not "greater or equal"). The first hit (most recently loaded) wins. No ALC awareness.
2. **`BaseUriHelper.GetLoadedAssembly(name, version, key)`** (`PresentationCore/System/Windows/Navigation/BaseUriHelper.cs`). For pack URIs: (1), then `Assembly.Load(asmName)`, which binds in the default context unless a contextual reflection context is set.
3. **`ResourceContainer`** (`PresentationFramework/MS/Internal/AppModel/ResourceContainer.cs`):
   - `GetResourceManagerWrapper` caches by lowercase name plus the URI's version and token strings;
   - `OnAssemblyLoadEventHandler`/`UpdateCachedRMW` retarget the cache to newly loaded assemblies;
   - `ResourcePart` hands out `BamlStream(stream, _rmWrapper.Assembly)`.
4. **`Application.LoadComponent(Object, Uri)`** (`PresentationFramework/System/Windows/Application.cs`). Throws `UriNotMatchWithRootType` ("The component '{0}' does not have a resource identified by the URI '{1}'.") when the stream's assembly differs from the component's.
5. **`XamlReader.LoadBaml`** (`PresentationFramework/System/Windows/Markup/XamlReader.cs`). `LocalAssembly = streamInfo.Assembly`, i.e. whichever copy (3) picked.
6. **`Baml2006SchemaContext.ResolveAssembly`**. Uses (1) with the BAML's full name, then `Assembly.Load(full)`, then a local-assembly short-name match, then `Assembly.Load(short)`. `Baml6Assembly.Assembly` does the same.
7. **`System.Xaml` `XamlSchemaContext.ResolveAssembly`** (`System.Xaml/System/Xaml/XamlSchemaContext.cs`), for loose XAML. Uses (1), then `Assembly.Load` with a short-name fallback.
8. **`ReflectionHelper.LoadAssemblyHelper`** (`Shared/System/Windows/Markup/ReflectionHelper.cs`), used by `XamlTypeMapper`/`XmlnsCache` (legacy parser paths). A process-wide cache keyed by **short name, first one wins**, which throws `ParserAssemblyLoadVersionMismatch` when a different version is requested later. Another way loose XAML can resolve "wrongly"; not exercised by the spike.
9. **`SystemResources`** (`PresentationFramework/System/Windows/SystemResources.cs`). Theme and `generic` dictionaries are loaded from the `Assembly` object (`LoadDictionary`) and cached per `Assembly`, which is correct per copy. But **external theme assemblies** (`ThemeInfo` with `ExternalAssembly`) are loaded with `Assembly.Load` in `LoadExternalAssembly`, which goes to the default context, so they fail for compartment-private libraries unless forwarded.
10. **`ResourceDictionary.Source` set in BAML.** `Baml2006ReaderInternal.CreateTypeConverterMarkupExtension` → `SourceUriTypeConverterMarkupExtension` → `BaseUriHelper.AppendAssemblyVersion` adds the local assembly's version when the URI names the BAML's **own** assembly. Only when `Switch.System.Windows.Baml2006.AppendLocalAssemblyVersionForSourceUri` is true, and it **defaults to false** (`PresentationFramework/System/AppContextDefaultValues.cs`). `AppContext.SetSwitch` overrides the default (`Common/src/System/LocalAppContext.cs`). Scenario 11 checks this.
11. **Markup compiler** (`PresentationBuildTasks/MS/Internal/MarkupCompiler/MarkupCompiler.cs`, resource locator generation). Adds `;V$(AssemblyVersion)` only if the `AssemblyVersion` property is non-empty when `MarkupCompilePass1` runs. The SDK infers `AssemblyVersion` from `Version` later, in the `GetAssemblyVersion` target (`Microsoft.NET.GenerateAssemblyInfo.targets`). The token is only added if `AssemblyPublicKeyToken` is set, which the SDK never does.
12. `grep -r AssemblyLoadContext` over PresentationFramework, PresentationCore, WindowsBase, System.Xaml, Shared and Common returns **no results**.

## 5. Windows run checklist

**Prerequisites**
1. Windows 10/11 x64 and the **.NET 10 SDK**: `dotnet --list-sdks` should show `10.0.x`. Built with 10.0.203.
2. The repo checked out. The spike folder alone is enough; it doesn't depend on anything else in the repo.

**Run (PowerShell)**

    cd <repo>\spike\wpf-across-load-contexts
    .\run-windows.ps1

Equivalent to:

    dotnet build src\WpfHost\WpfHost.csproj -c Debug
    cd src\WpfHost\bin\Debug\net10.0-windows
    .\WpfHost.exe --list          # scenario ids
    .\WpfHost.exe --all           # every scenario in its own process, ~1-2 s each
    .\WpfHost.exe --scenario 5    # a single scenario
    .\WpfHost.exe                 # interactive window: one button per scenario

If `run-windows.ps1` is blocked: `powershell -ExecutionPolicy Bypass -File .\run-windows.ps1`.

**What to expect.** Every check line should say `matches prediction`, the summary should end with `ALL PREDICTIONS MATCHED`, and the exit code should be 0. Predicted PASS/FAIL counts:

| Scenario | Predicted | Scenario | Predicted |
|---|---|---|---|
| 0 env | 1 PASS | 8 identical copies | 3 PASS, 2 FAIL (8.2, 8.3) |
| 1 (Q1) | 2 PASS | 9a XAML-only ref, no mitigation | 1 FAIL |
| 2 (Q2) | 5 PASS | 9b / 9c / 9d mitigations | 1 PASS each |
| 3 (Q3) | 4 PASS | 10a roll-forward, no mitigation | 1 FAIL |
| 4 (Q4) | 2 PASS | 10b / 10c mitigations | 1 PASS each |
| 5 (Q5) | 9 PASS, 2 FAIL (5.7, 5.8) | 11a switch off | 2 FAIL |
| 6 (Q6) | 3 PASS | 11b switch on | 1 PASS, 1 FAIL (11b.2) |
| 7 same AssemblyVersion | 2 PASS, 3 FAIL (7.2, 7.3, 7.4) | | |

PNG screenshots of each rendered view go to `results\scenario<id>-<name>.png`. Colours: red = v1, orange = v1b, green = v2.

**What to paste back**
1. The whole `src\WpfHost\bin\Debug\net10.0-windows\results\summary.txt`. At minimum, the `SUMMARY` block plus every check block containing `!! UNEXPECTED`, `CRASHED` or `TIMEOUT`.
2. Optionally `wpf-spike-results.zip` (the PNGs), if anything looks odd.
3. The scenario 0 block (Windows/runtime/WPF versions) is included in summary.txt.

**If something goes wrong:** for build errors on Windows, paste the `dotnet build` output. A scenario that hangs is killed after 90 s and reported as `TIMEOUT`.

## 6. Recommendations

### WPF add-on (`<Name>.Wpf`)
1. **Pre-load compartment-private assemblies** when a compartment is created, or at least those with `<Name>.g.resources` and those referenced by an assembly with BAML. This lets WPF's AppDomain-wide lookup find them (fixes scenario 9). It needs a core hook: an option `PreloadPrivateAssemblies` or a "compartment created" callback.
2. **Optional `Default.Resolving` forwarder**, registered per `PluginHost` and removed on dispose. It forwards default-context load failures to the **single** compartment that owns a private copy with that simple name, ignoring the version. This covers roll-forward (scenario 10) and external theme assemblies. If several compartments own a copy, it doesn't resolve and logs instead. Document it as process-global behaviour, the one deliberate opt-in exception to ADR 0012's "no global state".
3. Activate extensions inside `compartment.EnterContextualReflection()` in the WPF activator. It's cheap, but not sufficient on its own, because templates expand later.
4. **The pack URI builder always emits the version** taken from the loaded `Assembly`: `/Name;v1.2.3.4;component/path`, plus the token if strong-named. Resource-dictionary merging uses it.
5. **Host guidance:** set `Switch.System.Windows.Baml2006.AppendLocalAssemblyVersionForSourceUri=true`, e.g. `<RuntimeHostConfigurationOption Include="Switch.System.Windows.Baml2006.AppendLocalAssemblyVersionForSourceUri" Value="true" />` in the host csproj. It fixes a library's references to its *own* dictionaries (scenario 11b), not cross-assembly ones.
6. **Plugin guidance:** reference other assemblies' dictionaries through the add-on's helpers or versioned URIs, and keep XAML control libraries host-owned where possible.

### Plugin SDK (`<Name>.Sdk`)
- Set `AssemblyVersion` as an **explicit property at evaluation time** (e.g. derived from `Version` in props), so `InitializeComponent` URIs get `;V…;`. Verified: `-p:AssemblyVersion=3.4.5.0` produces `/X;V3.4.5.0;component/...`. This only helps plugin assemblies, not third-party libraries.

### Core: share identical XAML-bearing assemblies (bigger than a warning)
- When the same assembly identity (simple name + `AssemblyVersion` + public key token) would be compartment-private in more than one compartment, and the package ID and version are also the same, load it **once** and return that instance from the other compartments' `Load()`, as for plugin-provided assemblies. Without this, two plugins in default compartments that use the same control library break.
- Apply it at least to WPF-relevant assemblies (below). Applying it to all identical private packages is a separate decision, because it changes the isolation of static state.

### Duplicate-assembly warning rule (replaces the current wording)
Evaluate it statically at validation and at hot-install, from metadata only (`System.Reflection.Metadata`; `tools/XamlAsmInspect` is a working prototype), for every simple name that would end up in **two or more load contexts** (default included):
- **WPF-relevant:** any copy contains the manifest resource `<SimpleName>.g.resources`, or is referenced (AssemblyRef table) by an assembly in the same compartment that contains `.g.resources`.
- **Same identity** (name + `AssemblyVersion` + token equal):
  - Same package and version → don't warn; share it (above).
  - Otherwise → **warning "will break"**: WPF resolves every copy to the most recently loaded one, and earlier copies' views fail with "does not have a resource identified by the URI" or `InvalidCastException`.
  - At hot install this breaks a plugin that's already running, so it must be reported in the install result. Suggest `PendingRestart` with a reason, plus the warning.
- **Different `AssemblyVersion`** → **warning "may break"**: name-only lookups (unversioned `pack://`, cross-assembly `ResourceDictionary.Source`, loose XAML, `InitializeComponent` of assemblies built without an explicit `AssemblyVersion`) resolve to the most recently loaded copy.
  - Optional refinement: read the copy's `#US` (user strings) heap for `/<Name>;component/` (unversioned `InitializeComponent` locators) to report "will break" instead of "may break".
- Compare `AssemblyVersion`, never the NuGet version.
- Include the compartments, versions and file paths in the message.

## 7. Impact on the plan and ADRs

- **architecture.md §4 (WPF warning):** rewrite it. WPF resolves by simple name (+ `AssemblyVersion` + token if specified), process-wide, **last loaded wins**, and re-points cached lookups when a new copy loads. Same-identity duplicates (including identical package copies) **break** earlier copies, and different `AssemblyVersion`s break name-only lookups. "Different version" means `AssemblyVersion`, not NuGet version. Link this report.
- **architecture.md §5 (resolution order):**
  - Add step 3a: "compartment-private, and an identical copy (same package ID/version) of a WPF-relevant assembly is already loaded in another compartment → return that instance".
  - Add a note that WPF never calls a compartment's `Load()`; its fallbacks bind in the default context.
- **architecture.md §11 (WPF add-on):** pre-loading, the opt-in `Default.Resolving` forwarder, `EnterContextualReflection` in the activator, a versioned pack URI builder, AppContext switch guidance, the duplicate detector.
- **ADR 0004:**
  - Change the consequence "the library warns when a duplicated assembly contains WPF resources" to the rule in §6.
  - Add the decision "identical WPF-relevant private assemblies are shared across compartments", or put it in a new ADR. It's a real change to "one version per compartment / privacy per compartment".
  - Recommend that app developers declare WPF control libraries in `HostOwnedPackages` when they can.
- **ADR 0005 (hot install):** a hot install that introduces a same-identity duplicate of a WPF-relevant assembly affects already-loaded plugins. Treat it as a `PendingRestart` reason (with the warning) rather than loading silently.
- **ADR 0012:** record the `Default.Resolving` forwarder as an opt-in, per-`PluginHost` exception, registered and unregistered with the host.
- **implementation-plan.md:**
  - Phase 2 (SDK): set an explicit `AssemblyVersion` so generated XAML resource URIs are versioned.
  - Phase 3 (Compartment): a pre-load hook or option; sharing of identical WPF-relevant private assemblies; static WPF-relevance detection (`.g.resources`, AssemblyRefs).
  - Phase 4 (hot load): a same-identity WPF duplicate → `PendingRestart` with a reason.
  - Phase 7: the add-on items above. Extend "Done when" with: two plugins in separate compartments that use the same XAML control library both render, and a plugin whose control library is referenced only from XAML renders.
  - Risks table: add "WPF resolves assemblies by name process-wide (no ALC awareness)" with these mitigations.
- **open-questions.md:**
  - Close "WPF resources across load contexts" once the Windows run confirms.
  - Add "share identical private packages across compartments: WPF-only or all?".
- **plugin-developer-guide.md:** add a "WPF in plugins" section covering:
  - versioned URIs;
  - don't rely on XAML-only references being loaded (if the add-on doesn't pre-load);
  - the same control library in another plugin, with a different file but the same `AssemblyVersion`, will conflict.

## 8. How to re-run

**On Linux** (compile and static/runtime checks):

    cd spike/wpf-across-load-contexts
    export PATH=$HOME/.dotnet:$PATH
    dotnet build src/WpfHost/WpfHost.csproj                 # compiles everything incl. XAML/BAML, lays out plugins/
    dotnet build tools/XamlAsmInspect && dotnet build tools/AlcProbe
    P=src/WpfHost/bin/Debug/net10.0-windows/plugins
    dotnet tools/XamlAsmInspect/bin/Debug/net10.0/XamlAsmInspect.dll $P/*/*.dll   # identities, .g.resources, BAML assembly records
    dotnet tools/AlcProbe/bin/Debug/net10.0/AlcProbe.dll                           # runtime half of the predictions; prints ALL OK
    grep -rh "System.Uri(" --include=*.g.cs src/*/obj                              # generated InitializeComponent URIs
    cd tools/UriCodegenCheck && dotnet build && grep -rh "System.Uri(" obj          # unversioned by default
    dotnet build -p:AssemblyVersion=3.4.5.0 && grep -rh "System.Uri(" obj           # versioned

**On Windows:** see §5 (`run-windows.ps1`, or `WpfHost.exe --all` / `--scenario <id>`).

**WPF source used:** `git clone --depth 1 --branch release/10.0 --filter=blob:none --sparse https://github.com/dotnet/wpf`, sparse-checkout of `src/Microsoft.DotNet.Wpf/src/{PresentationFramework,PresentationCore,WindowsBase,Shared,System.Xaml,PresentationBuildTasks,Common}`, commit `b784c5bd8d2dddbf6a93444ad9bbdb5ab0e05363`.

## 9. Decisions made during the spike

- **The minimal loader isn't the real one.** It's a folder-per-compartment ALC with the architecture §5 order: host-owned (`HostSdk`) → null, imports (plugin-provided) → that assembly, file in folder → load, else null. That's enough, because WPF's behaviour doesn't depend on how the ALC resolves.
- **Every scenario runs in its own process**, to avoid order effects through WPF's static caches and the AppDomain assembly list.
- **The unversioned-URI case is simulated** (`UnversionedBadge` calls `LoadComponent` with `/SharedControls;component/...` itself) instead of building a fourth library variant. The generated code for that case was verified separately (`tools/UriCodegenCheck`).
- **PASS/FAIL means "WPF behaved correctly for the plugin"**, and the prediction is printed next to it. The run's exit code is about predictions matching, so an all-matched run with expected FAILs exits 0.
- **Load order is fixed per scenario** (A then B/C). By the same mechanism, the reverse order would break the other plugin; that wasn't run separately.
- **Not covered:** satellite (localised) BAML assemblies, external theme assemblies (`ThemeInfo` `ExternalAssembly`), `XmlnsDefinition` URL namespaces (`http://...`) mapped across duplicates, and collectible ALCs (not used by design).

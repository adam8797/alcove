# Implementation plan

> Status: **approved 2026-10-18.** Updated 2026-10-17 with the results of all five spikes. Changes made because of a spike are marked *(spike: name)*. See [What the spikes changed](#what-the-spikes-changed) for a summary.

This plan turns the design in [architecture.md](architecture.md) into working code. Terms are defined in [CONTEXT.md](../CONTEXT.md).

## Principles

- **Riskiest parts first.** Loading code (cells, load contexts, type identity) is the core of the library and the hardest to change later. It's built and tested against local fixtures before any feed code exists.
- **Every phase ends with something runnable** and tested end to end, not just unit tests.
- **Real packages in tests.** Integration tests build actual plugin projects, pack them into a local folder feed, and install from there. No mocked `AssemblyLoadContext`.
- **Real tools as test oracles** *(spike: nuget-resolution-and-assets)*. Resolution results are compared against `dotnet restore`, and asset selection against `dotnet build`'s `deps.json`.
- **Public API shaped by the guides.** The code samples in the app and plugin developer guides are the acceptance tests for the API's shape.

## Phase 0: Spikes ✅ (Windows verification outstanding)

Throwaway proof-of-concept code under `spike/<name>/`, described in [docs/spikes/](spikes/). Each has a `REPORT.md`.

| Spike | Result | Status |
|---|---|---|
| [cross-compartment-deps](../spike/cross-compartment-deps/REPORT.md) | Works if a plugin's **exposed dependencies** resolve through its cell. Leaks are detectable statically. | Done (Linux) |
| [hot-load-into-loaded-compartment](../spike/hot-load-into-loaded-compartment/REPORT.md) | The runtime doesn't pin "use the host's copy" answers, so cells must **freeze answers**. That gives a precise hot-load rule. | Done (Linux), 30/30 checks |
| [nuget-resolution-and-assets](../spike/nuget-resolution-and-assets/REPORT.md) | `RemoteDependencyWalker` + host pins matches `dotnet restore`; `ManagedCodeConventions` matches `dotnet build`. Single-file hosts have no inventory. | Done (Linux) |
| [nuget-signature-verification](../spike/nuget-signature-verification/REPORT.md) | Works on Linux only with the SDK root bundle shipped next to `NuGet.Packaging.dll`. Online revocation on a hanging network takes 120 s per package. | Done (Linux) |
| [wpf-across-load-contexts](../spike/wpf-across-load-contexts/REPORT.md) | Single copies work. Duplicate names break WPF, even identical copies. WPF never asks a cell for an assembly. | Built on Linux; **predictions need a Windows run** |

### Phase 0b: Windows verification (outstanding; blocks Phase 7)
- Run `spike/wpf-across-load-contexts/run-windows.ps1`. Expect `ALL PREDICTIONS MATCHED`. Any `!! UNEXPECTED` means revisiting §4/§5 of the architecture and ADR 0015.
- Run the signature spike's `Verifier matrix` on Windows.
- Check native soname/module-name collisions on Windows (hot-load spike `q6` scenarios).

## Phase 1: Repository foundation ✅

- Solution layout:
  ```
  src/Alcove.Abstractions/
  src/Alcove/                       core
  src/Alcove.Sdk/                   MSBuild props/targets (no assembly)
  src/Alcove.DependencyInjection/
  src/Alcove.Autofac/
  src/Alcove.Wpf/                   (Phase 7, not created yet)
  tests/Alcove.Tests/               unit tests
  tests/Alcove.IntegrationTests/    builds + packs fixture plugins, installs them
  tests/fixtures/                   fixture host contracts and plugin projects
  samples/ConsoleHost/, samples/WpfHost/ (Phase 7, not created yet), samples/plugins/
  ```
- `Directory.Build.props`: `net10.0`, nullable, warnings as errors, deterministic builds. SourceLink is off until the repository has a Gitea remote (Microsoft.SourceLink.Gitea).
- Central package management (`Directory.Packages.props`). Pin `System.Security.Cryptography.Pkcs` ≥ 10.0.13 *(spike: nuget-signature-verification)*.
- `global.json` pinning the SDK (the spikes used 10.0.203).
- Test framework: xUnit v3.
- CI on Gitea Actions: a Linux build + test job. A Windows job for WPF and signature tests once a Windows runner exists; until then, those are run manually.
- Versioning: one version for all packages, from a single property, prerelease (`0.x`) until the API settles.

**Done when:** an empty solution builds and runs an empty test in CI.

## Phase 2: Abstractions and SDK ✅

- `Alcove.Abstractions`: `IPlugin`, `IPluginContext`, `IExtensionRegistrar`, `ExtensionAttribute`, `ExtensionLifetime`, the plugin manifest model and schema version constant.
  - Depends on `NuGet.Versioning`, plus `Microsoft.Extensions.Logging.Abstractions` at the **lowest supported version** (host-owned; ADR 0009).
- `Alcove.Sdk` (props/targets only):
  - `PluginPackageType` → `<PackageType>`.
  - Generates `<id>.plugin.json` from `PluginCell`, `PluginIsolated`, `PluginPrivateDependency` items and an optional entry point, and packs it at the package root.
  - Keeps `Alcove.Abstractions`, its dependencies and every `PluginHostOwnedPackage` item (which a host contracts package adds from its `build/<id>.props`) as nuspec dependencies, but removes their runtime assets from the build output.
  - **Sets `AssemblyVersion` explicitly at evaluation time** (derived from `Version`), so WPF `InitializeComponent` URIs are versioned *(spike: wpf-across-load-contexts)*.
  - Validates the plugin properties (errors `ALCOVE001`–`ALCOVE006`). (A warning for dependencies that look host-provided was dropped.)
- Fixture plugins in `tests/fixtures/` built with the SDK.

**Done when:** `dotnet pack` on a fixture plugin produces a package with the right package type and manifest, without the contracts assemblies, and with an explicit `AssemblyVersion` (so XAML resource URIs are versioned) (all checked by tests).

## Phase 3: Loading (offline core) ✅

Everything needed to load plugins from a populated plugin root, with no feed access.

- **Options:** `PluginHostOptions` + `Load`/`Save` (System.Text.Json, source generated).
- **State file:** model (including `contentHash` and exposed dependencies), atomic writes, root lock (`.lock`, cross-process).
- **Host inventory** *(spike: nuget-resolution-and-assets)*:
  - read `APP_CONTEXT_DEPS_FILES` (app + framework `deps.json`s);
  - framework-provided pruning rule;
  - host framework from entry-assembly `TargetFramework`/`TargetPlatform` attributes;
  - host-owned list;
  - single-file host → clear "unsupported" error.
- **Asset selection:** `ManagedCodeConventions` + `FindBestItemGroup` with an embedded `PortableRuntimeIdentifierGraph.json`. Test oracle: `dotnet build`'s `deps.json`.
- **Assembly classification** and **cell assignment** (app options → manifest request → default; isolated rule).
- **Cell** (`AssemblyLoadContext` subclass):
  - **Resolution order** from architecture §5: frozen answer → host-owned → transitive plugin closure + exposed dependencies → cell-private → host-shared → unknown *(spike: cross-compartment-deps)*.
  - **Answer freezing:** Plan/Given/Committed records, one lock shared with install *(spike: hot-load-into-loaded-compartment)*.
  - **Version check:** reject assemblies lower than requested *(spikes: both)*.
  - Native libraries with soname-collision warning, and satellite assemblies.
  - A "cell created" hook for the WPF add-on's pre-loading *(spike: wpf-across-load-contexts)*.
  - **Shared loading of identical WPF-relevant private assemblies** ([ADR 0015](adr/0015-share-identical-wpf-assemblies.md)). Confirm against the Phase 0b Windows run.
- **Exposed-dependency scanner** (`System.Reflection.Metadata`; port from `spike/cross-compartment-deps/src/LeakScan`) *(spike: cross-compartment-deps)*.
- **WPF relevance and duplicate detector** (`System.Reflection.Metadata`; port from `spike/wpf-across-load-contexts/tools/XamlAsmInspect`), giving "will break" / "may break" findings *(spike: wpf-across-load-contexts)*.
- **Validation** (local):
  - state vs. disk (size + timestamp);
  - contracts compatibility and host-owned conflicts;
  - cell rules;
  - dependency graph;
  - `ExposedDependencyConflict`;
  - WPF duplicates;
  - manifest schema.

  Returns a structured report.
- **Plugin activation:** topological order, entry point or `[Extension]` scanning, `Register`, `InitializeAsync`, faults, `FailFast`.
- **Extension catalog:** `GetAll`, metadata, lifetimes, `Observe`, `ExtensionsAdded`. Activator abstraction.
- **`PluginHost`:** `Create`, `ValidateAsync`, `LoadAsync`, `Plugins` (status, fault), `DisposeAsync`.
- Test helper (internal for now): populate a plugin root from fixture `.nupkg` files without a feed. This later grows into `Alcove.Testing`.
- **Spike scenarios ported as integration tests:**
  - `q1-return-existing-lower`, `q2-split-identity`, `q3-failure-then-available`, `q6-native-remap` (hot-load spike);
  - Q2b/Q2c/Q4 scenarios (cross-cell spike).

**Done when:** a console sample loads these fixture plugins, lists their extensions, and a faulting plugin doesn't stop the rest:
- two plugins in separate cells with conflicting versions of the same dependency;
- one plugin depending on another across cells, with an exposed dependency;
- a chain of three plugins.

Validation also reports `ExposedDependencyConflict` for a fixture built to trigger it.

## Phase 4: Feeds and install ✅

- Feed configuration (`PackageFeed`, optional `nuget.config`), NuGet source repositories, `SourceCacheContext`. Bypass NuGet's user-wide HTTP cache (`NoCache` + per-operation temp folder; ADR 0012) *(spike: nuget-resolution-and-assets)*.
- Authentication: `PackageFeed.Credentials`, `nuget.config` credentials, default network credentials, and an app-supplied `ICredentialProvider`. NuGet's plugin credential providers aren't supported in v1.
- `SearchAsync` (server-side `SearchFilter.PackageType`; group local-feed hits by ID; compatibility flag with reason), `GetVersionsAsync`.
- **Resolution** with `RemoteDependencyWalker` + a feed-backed `IRemoteDependencyProvider`, host pins, re-walk on host-shared downgrade, `VersionConflicts` → install/update failures with reasons ([ADR 0016](adr/0016-resolution-with-dependency-walker.md)). Test oracle: `dotnet restore`.
- **Download** with `PackageExtractor.InstallFromSourceAsync` into the lowercase layout. Keep the `.nupkg`; pin `contentHash`.
- **Trust policy** *(spike: nuget-signature-verification)*:
  - `TrustPolicy` / `AllowedSigner` as in architecture §12;
  - `PackageSignatureVerifier` with the three providers, run before extraction;
  - map NU30xx codes and `Trust` statuses to our own failure reasons;
  - **ship the SDK's `trustedroots/*.pem`** in the core package, with `buildTransitive` targets that copy them next to `NuGet.Packaging.dll`;
  - call `InitializeForDotNetSdk()`;
  - `AllowedSigner.NuGetOrg(owners)`;
  - integration tests reusing the spike's fixtures (self-signed, tampered, unsigned).
- **`InstallAsync`** end to end, including the **hot-load rule** (architecture §6): joint re-resolution, committed-answer check, `PendingRestart` with the conflicting names, and the exposed-dependency and WPF-duplicate reasons *(spikes: hot-load-into-loaded-compartment, cross-compartment-deps, wpf-across-load-contexts)*.
- Prerelease handling, version ranges, `IProgress<T>` (including a "verifying signatures" step), cancellation everywhere.

**Done when:**
- the console sample installs a plugin from a local folder feed and from a private HTTP feed (a throwaway local NuGet v3 HTTP feed on localhost in tests, including an authenticated variant) while running, and it appears in the catalog without a restart;
- installing a plugin that needs a private version of a name its cell already took from the host gives `PendingRestart` with that reason;
- a signed-package policy accepts and rejects the spike's fixtures correctly on Linux.

## Phase 5: Lifecycle ✅

- Staged changes: `UpdateAsync`, `UninstallAsync` (blocked when other plugins depend on it, `cascade` option), `EnableAsync`/`DisableAsync`, `RollbackAsync`.
- Applying staged changes at startup, before validation.
- Keeping N previous versions; cleanup after a successful start.
- `GetPendingChangesAsync` (including `PendingRestart` installs with their reasons, and Faulted plugins whose missing dependency arrived: "will load at next startup"), `CheckForUpdatesAsync`, `RestoreAsync`, `VerifyAsync` (re-hash + optional signature re-verification of kept `.nupkg` files).
- Multi-process tests: two hosts on one root.

**Done when:** an integration test performs install → update → restart → rollback → restart → uninstall, with a second host instance seeing pending changes.

## Phase 6: Dependency injection add-ons ✅

- `Alcove.DependencyInjection`: `AddPlugins`, a container-based activator (`ActivatorUtilities`), `IServicePlugin`, cell service providers with fallback to the host, disposal order.
- `Alcove.Autofac`: the same model with Autofac (a child lifetime scope per cell, which fits naturally).
- A shared test suite run against both adapters, to keep them equivalent.

**Done when:** extension constructors receive host and cell services under both containers, including for hot-installed plugins.

## Phase 7: WPF add-on

> **On hold (2026-10-18):** skipped until the WPF spike has been run on Windows (Phase 0b). The Phase 3 hooks it depends on (the "cell created" callback, WPF relevance detection, shared loading of identical WPF-relevant assemblies) are still built in Phase 3.

- Dispatcher-marshalling catalog wrapper.
- Resource dictionary merging on plugin load.
- **Versioned** `pack://` URI builder, typed `ResourceKey` helpers.
- Linking view-models to views by convention through DataTemplates.
- *(spike: wpf-across-load-contexts)*:
  - **Pre-loading** of WPF-relevant cell-private assemblies, via the Phase 3 hook.
  - The **opt-in `Default.Resolving` forwarder** (per `PluginHost`).
  - `EnterContextualReflection` in the activator.
  - Host guidance for the `AppendLocalAssemblyVersionForSourceUri` switch.
- WPF sample host with plugins that contribute windows, controls and styles, using an app-provided `WpfHost.Sdk` (host-owned) with shared UserControls.

**Done when** (verified on Windows):
- the WPF sample hot-installs a plugin whose window opens with the app's styles and the plugin's own resources;
- two plugins in separate cells that use the same XAML control library both render;
- a plugin whose control library is referenced only from XAML renders.

## Phase 8: Hardening and documentation ✅ (except publishing, which waits for the user)

- Review the public API against the guides.
- XML docs on all public APIs; update `docs/` to match what was built.
- Logging review (event IDs, levels).
- Performance check: startup time with 50 plugins (budget for the exposed-dependency scan is at install, not startup).
- Refresh process for the bundled trusted roots (tied to SDK/NuGet updates).
- First prerelease packages to the Gitea feed.

## Later (after v1)

`Alcove.Wpf.Manager`, `Alcove.Tool`, `Alcove.Testing` (promote the internal helper), `Alcove.Templates`, optional plugin dependencies, `IConfiguration` binding add-on, `TrustPolicy.FromNuGetConfig`, strict revocation, single-file host support (shipped `deps.json`), GitHub mirror, nuget.org publishing.

## Risks

| Risk | Mitigation |
|---|---|
| Cross-cell type identity | Spike done: exposed-dependency rule + static scanner + `ExposedDependencyConflict` *(spike: cross-compartment-deps)* |
| Silent identity splits after hot installs | Answer freezing + hot-load rule; spike scenarios as regression tests *(spike: hot-load-into-loaded-compartment)* |
| **WPF resolves assemblies by name process-wide**, with no load-context awareness | Duplicate detector, versioned URIs, pre-loading, forwarder, sharing of identical WPF-relevant packages (ADR 0015); **Windows verification in Phase 0b** *(spike: wpf-across-load-contexts)* |
| NuGet client libraries are large and poorly documented | Spike done: the APIs to use are identified; wrap them behind internal interfaces *(spike: nuget-resolution-and-assets)* |
| Asset selection differs subtly from `dotnet build` | Spike: matched in all cases with the right framework; `deps.json` comparison as a test oracle; framework from entry-assembly attributes |
| Linux trust store rejects nuget.org signatures | Solved by shipping the SDK root bundle; needs a refresh process *(spike: nuget-signature-verification)* |
| Online revocation on a hanging network: about 120 s per package, can't be cancelled | Default stays `Online` (decided); document `Offline` for air-gapped hosts; verify off the UI thread with progress |
| Process-wide side effects (trust store, WPF forwarder; the HTTP cache is bypassed) | Documented in ADR 0012; forwarder opt-in |
| Native libraries share dependencies by soname across cells | Warn on collisions; Windows check in Phase 0b |
| Scope creep | Later items stay out of v1 |

## What the spikes changed

- **New concepts:** exposed dependencies (ADR 0014), answer freezing and the precise hot-load rule (ADR 0005 amendment), WPF-relevant assemblies and duplicate detection (ADR 0004 amendment).
- **Decisions taken 2026-10-18:** share identical WPF-relevant private assemblies only (ADR 0015); MELA in Abstractions accepted at the lowest supported version (ADR 0009); revocation defaults to Online (ADR 0010); Faulted plugins are never re-activated during a run (ADR 0005, 0011).
- **NuGet details settled:** `RemoteDependencyWalker` instead of `PackageResolver` (ADR 0016); `contentHash` as the pinned hash; lowercase package folders; framework from entry-assembly attributes; framework-provided packages skipped.
- **Trust:** concrete `TrustPolicy` shape; the library ships the root bundle; process-wide `InitializeForDotNetSdk` (ADR 0010, 0012 amendments).
- **Scope:** single-file hosts unsupported in v1; Phase 0b Windows verification added; Phase 2 SDK sets `AssemblyVersion`; Phase 3 gains a scanner, a detector, freezing and a version check; Phase 4 gains root-bundle packaging and the hot-load rule.

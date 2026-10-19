# Spike report: NuGet resolution and asset selection

> Written by the orchestrator. The spike agent's environment didn't allow it to save this file, and its full draft couldn't be sent back. This report is put together from the agent's summary and findings, checked against the evidence logs in [`results/`](results/) (copied from the gitignored `work/logs/`) and the code in `src/Spike/`. Section-level detail is in those files.

Run on Linux x64 with .NET SDK 10.0.203 (runtime 10.0.7) and NuGet client libraries **7.9.0** (latest stable on 2026-10-17).

## Summary

1. **Host inventory works with System.Text.Json only.** `APP_CONTEXT_DEPS_FILES` lists the app's `deps.json` plus one `deps.json` per *resolved* shared framework, and the path gives the framework name and exact version. Framework assemblies come from those framework files (or from a `runtimepack.*` library when self-contained). **Single-file hosts expose nothing**: no deps file and an empty TPA list.
2. **Resolution: use `RemoteDependencyWalker` from NuGet.DependencyResolver.Core** (PackageReference semantics), not `PackageResolver` from NuGet.Resolver. A ~60-line `IRemoteDependencyProvider` over `FindPackageByIdResource` is enough; NuGet.Commands isn't needed. It matched `dotnet restore` exactly in all 4 cases tried.
3. **Host packages are pinned at a synthetic root**, so the host's version wins wherever it satisfies the plugin. If the walker has to *downgrade*, that's a conflict:
   - host-owned → the plugin is incompatible;
   - host-shared → the package is unpinned, the graph is walked again, and it becomes compartment-private.
4. **Multi-plugin compartments:** a synthetic root depending on all of the compartment's plugins gives one version per package. Range conflicts are reported by `Analyze().VersionConflicts`. `PackageResolver` also works, but gathered 2554 candidate versions for a 4-package graph, and its only error is "Unable to resolve dependencies."
5. **Asset selection:** `ManagedCodeConventions` + `ContentItemCollection.FindBestItemGroup`, with an embedded `PortableRuntimeIdentifierGraph.json`, matched `dotnet build`'s `deps.json` in every package × target combination tried. **Trap:** `deps.json` drops the platform version (`net10.0-windows7.0` becomes `net10.0-windows`), which picks the wrong assets. Read the framework from the entry assembly's `[TargetFramework]` + `[TargetPlatform]` attributes instead.
6. **Download:** `PackageExtractor.InstallFromSourceAsync` + `VersionFolderPathResolver(root, isLowercase: true)` reproduce the global-packages layout, including `.nupkg.metadata`. **Signed packages have two different SHA-512s:**
   - `contentHash`: excludes the signature; recorded by `.nupkg.metadata` and the host's `deps.json`;
   - the file hash: recorded by `.nupkg.sha512` and the nuget.org catalog.
7. **PackageType search:** `SearchFilter.PackageType` is filtered server-side by nuget.org, and also by the local folder feed. The local feed returns each version as a separate hit.
8. **Size:** NuGet.Protocol + NuGet.DependencyResolver.Core is **3.4 MB in 11 dlls**, including Newtonsoft.Json (≈700 KB, pulled in by NuGet.Packaging). NuGet.Resolver would add 65 KB; NuGet.Commands ≈1 MB and 4 dlls.

## Q1. Host inventory

Code: `src/Spike/HostInventory.cs`, `InventoryExtras.cs`. Evidence: `results/q1-inventory.txt` (console host), `results/q1-inventory-web.txt` (ASP.NET Core host, two shared frameworks).

- `AppContext.GetData("APP_CONTEXT_DEPS_FILES")` is **`;`-separated on every OS**. That's different from `TRUSTED_PLATFORM_ASSEMBLIES`, which uses `Path.PathSeparator` (`:` on Linux).
- The first entry is the app's `deps.json`. Each further entry is a framework's `deps.json` at `.../shared/<FrameworkName>/<version>/<FrameworkName>.deps.json`, which gives the **resolved** framework name and version (e.g. `Microsoft.NETCore.App 10.0.7`). The web host lists both `Microsoft.NETCore.App` and `Microsoft.AspNetCore.App`.
- The app's `deps.json` `libraries` section gives package ID → version (`type: package`), and the `targets` section gives assembly → package. Example from the console host: `Serilog 3.1.1 → asm Serilog 2.0.0.0`. Package version and `AssemblyVersion` differ, so the host inventory must keep both.
- **Framework-provided packages** (e.g. `System.Text.Json` 9.0.0 referenced on net10.0) are *pruned* from `deps.json` by the .NET 10 SDK. The runtime rule "package ID equals a framework assembly name and package major ≤ framework major → provided by the framework, skip it" matched 271/272 and 139/140 entries of the SDK's own pruning lists. That's the approach to take: treat these as host-owned framework assemblies and **skip** them rather than version-check them.
- **Self-contained hosts** have no framework `deps.json`; framework assemblies appear as a `runtimepack.*` library in the app's `deps.json`.
- **Single-file hosts:** `APP_CONTEXT_DEPS_FILES` is empty and the TPA list is empty, so there's nothing to build an inventory from. Options: document as unsupported (clear error at `PluginHost.Create`), or require the app to ship its `deps.json` next to the executable.
- **Host framework** (for asset selection) must come from the entry assembly's attributes, not from `deps.json` (see Q4).

## Q2. Resolution with host constraints

Code: `src/Spike/Feeds.cs`, `Resolution.cs`. Evidence: `results/q2-resolve.txt`, `results/q2-restore-oracle.txt`.

- **Use `RemoteDependencyWalker`** (NuGet.DependencyResolver.Core): the same graph walker restore uses, with PackageReference "nearest wins / lowest applicable" semantics. It needs an `IRemoteDependencyProvider`. A ~60-line implementation over `FindPackageByIdResource` (versions + nuspec dependencies) is enough, so NuGet.Commands isn't needed.
- **Host constraints:** create a synthetic root that depends on the plugin(s) *and* pins every host package at its exact version. Nearest-wins then makes the host's version win whenever the plugin's range allows it.
- **Conflict detection:** when a plugin needs more than the host has, the walker reports a downgrade of the pinned host package. Then:
  - **host-owned** → fail: plugin incompatible with this host;
  - **host-shared** → remove the pin, walk again; the package resolves normally and is classified **compartment-private**.
- Example (`q2-resolve.txt`): `Serilog.Sinks.File 7.0.0` against a host with `Serilog 3.1.1` → walk 1 finds the host-shared Serilog doesn't satisfy `>= 4.2.0`, unpins it, walk 2 resolves `Serilog 4.2.0` as `PrivateConflict`. 2 walks, 95 ms.
- Example: `Microsoft.Extensions.Http 10.0.13` against a host on `Microsoft.Extensions.* 8.0.0` → the M.E.* packages become private. But **`Microsoft.Extensions.Logging.Abstractions` is host-owned** in this design (needed by `IPluginContext.Logger`), so the plugin would be **incompatible**. See "Contradictions" below.
- **Oracle:** the resolved sets for `Serilog.Sinks.File 7.0.0`, `Microsoft.Extensions.Http 10.0.13` and `8.0.0`, and `NuGet.Packaging 6.0.0` match a real `dotnet restore` exactly (`q2-restore-oracle.txt`).
- **`dotnet tool install`** doesn't resolve a dependency graph at all: tools are self-contained packages. It downloads the one package and selects its assets, so it's not a model for us.

## Q3. Multi-plugin resolution per compartment

Code: `src/Spike/Diamond.cs`. Evidence: `results/q3-diamond.txt`. Fixtures: local feed with PluginA/B/C and LibShared 1.0/2.0.

- **Diamond** (A needs `LibShared >= 1.0.0`, B needs `>= 2.0.0`; Newtonsoft `>= 12.0.1` vs `>= 13.0.1`): one walk from a root depending on both plugins gives `LibShared 2.0.0` (private) and `Newtonsoft.Json 13.0.3` (host-provided, since it satisfies both). The diamonds are reported so the library can log its warning.
- **Range conflict** (B `>= 2.0.0` vs C `[1.0.0, 2.0.0)`): detected via the walker's `Analyze().VersionConflicts`, which names the package and both ranges, so it can be turned into a clear validation error.
- **`PackageResolver`** (NuGet.Resolver, packages.config semantics) gets the same answers for satisfiable cases, but gathers every candidate version (2554 for this graph) and only says "Unable to resolve dependencies." on conflicts. **Don't use it.**

## Q4. Asset selection

Code: `src/Spike/AssetSelection.cs` (+ embedded `PortableRuntimeIdentifierGraph.json`). Comparison apps: `compare/`. Evidence: `results/q4-assets.txt`, `results/build-compare.txt`.

- Use `ManagedCodeConventions` with `ContentItemCollection.FindBestItemGroup` for each pattern set (runtime assemblies, `runtimes/<rid>/lib`, `runtimes/<rid>/native`, resource/satellite assemblies). Pass a `RuntimeGraph` loaded from the embedded `PortableRuntimeIdentifierGraph.json` (the same file the SDK uses).
- Compared against `dotnet build`'s `deps.json` for `linux-x64`, `linux-musl-x64`, `win-x64`, `osx-arm64`, portable, `net10.0-windows7.0`, and `net10.0-windows10.0.19041.0` on `win-x64`, using packages with native and RID-specific assets (e.g. `Microsoft.Data.Sqlite` / `SQLitePCLRaw.lib.e_sqlite3`) and satellite assemblies. **All combinations match when the correct framework is used.**
- **The trap, shown deliberately in the log:** selecting with `net10.0-windows` (what `deps.json` says) instead of `net10.0-windows7.0` (what the app actually targets) picks `lib/net8.0/` instead of `lib/net8.0-windows7.0/` for two packages. These are the "2 differences" in `q4-assets.txt`. **Read the host framework from the entry assembly's `TargetFrameworkAttribute` + `TargetPlatformAttribute`**. The spike's helper `HostInventory.FromAttributes` (in `HostInventory.cs`) combines `NuGetFramework.ParseFrameworkName` with the platform name and version.
- Packages pruned by the SDK because the framework provides them (e.g. `System.Threading.Tasks.Extensions 4.5.4`, `lib/netcoreapp2.1/_._`) select an empty placeholder group. They must be skipped via the framework-provided rule in Q1.

## Q5. Download and extraction

Code: `src/Spike/Download.cs`. Evidence: `results/q5-download.txt`.

- `PackageExtractor.InstallFromSourceAsync` with `VersionFolderPathResolver(root, isLowercase: true)` produces the NuGet global-packages layout: **lowercase** `packages/<id>/<version>/`, with `<id>.<version>.nupkg`, `.nupkg.sha512`, `.nupkg.metadata`, the nuspec, `.signature.p7s` and extracted content. It works for nuget.org and local folder feeds.
- **Two hashes** for signed packages (e.g. Serilog 3.1.1):
  - `.nupkg.metadata` `contentHash` = `PackageArchiveReader.GetContentHash()` = the host `deps.json` `sha512`. This is computed **without** the signature file.
  - `.nupkg.sha512` = SHA-512 of the file bytes = the nuget.org catalog `packageHash`.
  - For unsigned packages the two are equal.
- **Recommendation:** pin `contentHash` (it's stable across re-signing and matches what `deps.json` records), and keep the `.nupkg` file so the signature can be re-verified (see the signature spike) and `VerifyAsync()` can re-hash.
- **User-wide HTTP cache:** NuGet writes feed responses to the user-wide HTTP cache by default. The agent reported that the only full override was the process-wide `NUGET_HTTP_CACHE_PATH` environment variable. The spike code also has a partial option: `SourceCacheContext { NoCache = true, GeneratedTempFolder = ... }`, enabled with `SPIKE_NOCACHE=1`. Recommendation: document the cache as acceptable (it's a cache, not state), and confirm the exact behaviour in Phase 4.

## Q6. PackageType filtering

Code: `src/Spike/Search.cs`. Evidence: `results/q6-search.txt`.

- `PackageSearchResource.SearchAsync` with `new SearchFilter(includePrerelease: false) { PackageType = type }`. nuget.org's V3 search filters **server-side** (`SupportsPackageTypeFiltering=True`): `packageType=DotnetTool` and `Template` return only those types, and an unknown type (`MyAppPlugin`) returns no results.
- The local folder feed (`LocalPackageSearchResource`) also honours the filter, but returns **every version as a separate hit**, so group by ID client-side.

## Q7. Size

Evidence: `results/q7-size.txt` (`size/measure.sh`).

| Set | Size | DLLs |
|---|---|---|
| NuGet.Versioning only | 61 KB | 1 |
| NuGet.Packaging (+ deps) | 2.2 MB | 8 |
| NuGet.Protocol (+ deps) | 3.3 MB | 9 |
| **Protocol + DependencyResolver.Core (recommended)** | **3.4 MB** | **11** |
| + NuGet.Resolver | 3.5 MB | 12 |
| NuGet.Commands | 4.5 MB | 15 |

Newtonsoft.Json (≈700 KB) comes in through NuGet.Packaging.

## Recommended core dependencies

- `NuGet.Protocol` (brings Packaging, Configuration, Frameworks, Common, Versioning)
- `NuGet.DependencyResolver.Core` (brings LibraryModel)
- **Not** NuGet.Resolver, **not** NuGet.Commands.
- `Microsoft.Extensions.Logging.Abstractions`
- Pin `System.Security.Cryptography.Pkcs` ≥ 10.0.13 (see the signature spike).

## Contradictions with the architecture doc

1. **§1 dependencies:** lists "NuGet.Protocol / Packaging / Resolver". Should be Protocol + DependencyResolver.Core.
2. **Abstractions dependencies:** `IPluginContext.Logger` is an `ILogger`, so Abstractions also depends on `Microsoft.Extensions.Logging.Abstractions`, which then must be host-owned. That contradicts "Abstractions depends only on NuGet.Versioning". It also makes any plugin needing a newer `Microsoft.Extensions.Logging.Abstractions` than the host has (e.g. `Microsoft.Extensions.Http 10` in a host on 8.0) **incompatible**. This needs an explicit decision.
3. **§4:** framework-provided packages should be **skipped** (as the SDK does), not version-checked against the host.
4. **§5:** "dependencies are resolved together with NuGet's resolver" should become "walker + host pins + re-walk on host-shared conflicts". The host framework comes from entry-assembly attributes. The RID graph is embedded.
5. **§2:** package folders are lowercase; keep `.nupkg` and `.nupkg.metadata`.
6. **§12 / ADR 0010:** state which hash is pinned (recommend `contentHash`).

## Impact on the plan and ADRs

- **architecture.md:** apply the contradictions above.
- **ADR 0012:** note that NuGet uses the user-wide HTTP cache by default (process-wide `NUGET_HTTP_CACHE_PATH` is the only override); document it as acceptable.
- **implementation-plan.md:**
  - Phase 3: host inventory from `APP_CONTEXT_DEPS_FILES` + entry-assembly framework attributes; framework-provided pruning rule; asset selection with `ManagedCodeConventions` + embedded RID graph; single-file hosts → explicit "unsupported" error (or a shipped `deps.json`).
  - Phase 4: resolution with `RemoteDependencyWalker` + host pins + re-walk; `VersionConflicts` → validation errors; download with `PackageExtractor` into the lowercase layout; pin `contentHash`.
  - Tests: use `dotnet restore` results and `dotnet build`'s `deps.json` as **test oracles**, as this spike did.
- **Decision needed:** whether Abstractions should depend on `Microsoft.Extensions.Logging.Abstractions` (see Contradiction 2).

## Not covered

- The legacy `runtime.json` mechanism for RID-specific *dependencies*; modern packages don't use it.
- Running on Windows or under WPF.

## How to re-run

```bash
cd spike/nuget-resolution-and-assets
./run-all.sh                 # builds fixtures, comparison apps and the spike; runs every experiment; logs to work/logs/
cp work/logs/*.txt results/  # results/ is the committed copy of the evidence
```

A local `nuget.config` keeps downloads under `work/` (gitignored). Downloaded packages are treated as data only and never executed.

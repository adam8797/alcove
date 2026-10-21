# Architecture

Terms in **bold** are defined in [CONTEXT.md](../CONTEXT.md).

> Updated 2026-10-17 with the results of the five spikes in [`spike/`](../spike/). Changes based on a spike are marked *(spike: name)*. Decisions taken on 2026-10-18 are noted where they apply. §11 was rewritten on 2026-10-19 to describe the `Alcove.Wpf` add-on as built (Phase 7).

## 1. Package layering

```
                 ┌──────────────────────┐
 plugin code ──▶ │ Alcove.Abstractions  │ ◀── also referenced by the core
                 └──────────────────────┘
                            ▲
                 ┌──────────┴───────────┐
 host app ─────▶ │ Alcove (core)        │ ── NuGet.Protocol + NuGet.DependencyResolver.Core
                 │                      │ ── Microsoft.Extensions.Logging.Abstractions
                 └──────────────────────┘
                     ▲        ▲       ▲
        ┌────────────┘        │       └─────────────┐
 Alcove.DependencyInjection  Alcove.Autofac   Alcove.Wpf ◀── Alcove.Wpf.Manager (later)
```

- **`Alcove.Abstractions`** is the only library package a plugin references at runtime. It changes rarely and is backward compatible within a major version ([ADR 0009](adr/0009-separate-abstractions-package.md)). Its dependencies are `NuGet.Versioning` (version types) and `Microsoft.Extensions.Logging.Abstractions` (for `IPluginContext.Logger`). All three are always **host-owned assemblies**: loaded once, from the default context. Making logging host-owned means a plugin needing a *newer* `Microsoft.Extensions.Logging.Abstractions` than the host has is incompatible. This is accepted and documented: Abstractions references the lowest supported version, and the host's version caps what plugins can use ([ADR 0009](adr/0009-separate-abstractions-package.md)) *(spike: nuget-resolution-and-assets)*.
- **`Alcove.Sdk`** is build-time only (`PrivateAssets="all"`). It contains MSBuild props/targets and nothing that ships in the plugin.
- **Add-ons** depend on the core only, never on each other. Each one plugs into a core extension point: DI add-ons use `IExtensionActivator` and `ICellServicesFactory` (§10), and `Alcove.Wpf` (`net10.0-windows`) uses `ICellObserver` (§11). Each add-on is made host-owned automatically when it's set up, and its buildTransitive props do the same for plugin projects.
- **Core** uses no DI library and no UI framework, and has no static or global state of its own. Several `PluginHost` instances can exist in one process. Documented process-wide exceptions are listed in [ADR 0012](adr/0012-app-supplies-root-no-global-state.md): NuGet's trust store selection and the opt-in WPF resolving forwarder. NuGet's user-wide HTTP cache is bypassed.
- **Namespaces follow feature folders.** Only entry points are in a package's root namespace: `PluginHost`, `PluginHostOptions`, `PluginInfo` and the plugin contracts in `Alcove` (so a plugin needs only `using Alcove;`), and each add-on's setup extension methods in `Alcove.DependencyInjection`, `Alcove.Autofac` and `Alcove.Wpf`. Everything else, public and internal together, is in the namespace of its feature folder: `Alcove.Catalog`, `Alcove.Cells`, `Alcove.Feeds`, `Alcove.Resolution`, `Alcove.Install`, `Alcove.Lifecycle`, `Alcove.Trust`, `Alcove.Validation`, `Alcove.Loading`, `Alcove.Hosting`, `Alcove.Packaging`, `Alcove.Inspection` and `Alcove.Storage` in the core; `Alcove.Wpf.Resources`, `Alcove.Wpf.Views`, `Alcove.Wpf.Threading` and `Alcove.Wpf.Loading` in the WPF add-on; `*.Composition` in the DI add-ons (CONTRIBUTING.md).
- **NuGet client dependencies:** `NuGet.Protocol` (which brings Packaging, Configuration, Frameworks, Common, Versioning) and `NuGet.DependencyResolver.Core`, about 3.4 MB in 11 DLLs. Not NuGet.Resolver, not NuGet.Commands. `System.Security.Cryptography.Pkcs` is pinned to ≥ 10.0.13 (the version NuGet.Packaging brings has known vulnerabilities) *(spikes: nuget-resolution-and-assets, nuget-signature-verification)*.

## 2. The plugin root

The app always supplies the root directory ([ADR 0012](adr/0012-app-supplies-root-no-global-state.md)). Layout:

```
<root>/
├── config.json              configuration file (optional; read only if the app loads it)
├── state.json               state file (written only by the library)
├── .lock                    root lock, held while changing state
├── .inuse/                  one record per running PluginHost: <pid>-<start>-<guid>.json listing the
│                            package folders it uses; cleanup never deletes those while the process lives
├── packages/
│   ├── <id-lowercase>/<version>/   extracted packages: plugins and their dependencies
│       ├── <id-lowercase>.<version>.nupkg   kept for signature re-verification and full re-hash
│       ├── .nupkg.metadata         contains contentHash
│       ├── <id-lowercase>.nuspec
│       ├── <PackageId>.plugin.json (plugin packages only)
│       ├── lib/<tfm>/...
│       └── runtimes/<rid>/...
│   └── .deleted/                   folders being removed after a successful start (retried next start)
└── data/
    └── <plugin-id>/         per-plugin private data directory (IPluginContext.DataDirectory)
```

- Packages are stored by lowercase ID and version, exactly like NuGet's global packages folder. `PackageExtractor.InstallFromSourceAsync` with `VersionFolderPathResolver(root, isLowercase: true)` produces this layout *(spike: nuget-resolution-and-assets)*. A dependency used by several plugins is stored once on disk; it may still be *loaded* into several cells.
- An update downloads the new version next to the old one. Switching versions is just a change in `state.json`. Old versions are kept for **rollback** (one previous version by default, configurable) and cleaned up after a successful startup.
- `state.json` is written atomically (write to a temp file, then rename) while holding `.lock`.

### State file contents

For each installed plugin: ID, active version, previous versions kept, enabled flag, **staged change** (if any), package hash, size and timestamp of each extracted file, the resolved dependency set (package ID, version, hash, and whether it's host-owned, host-shared or cell-private), a record per assembly (name, version, and whether it's **WPF-relevant**), and the plugin's **exposed dependencies** (§5). Staged changes are stored per plugin: a version switch (`Update`, `Rollback`) carries the full record of the target version, an uninstall is a staged change of its own, and enable/disable is stored separately so it can combine with a switch. Previous versions are kept as full records, so rollback needs no network. Assets are re-selected from the package folder at load time; the state file doesn't store asset paths. Also: the cell assignment, and the manifest schema version of each plugin.

The pinned hash is NuGet's **`contentHash`**: SHA-512 computed without the package signature, as recorded in `.nupkg.metadata` and in a host's `deps.json`. Signed packages also have a different whole-file SHA-512 (the one in `.nupkg.sha512` and the nuget.org catalog). `contentHash` stays stable if a package is re-signed *(spike: nuget-resolution-and-assets)*.

## 3. Identifying a plugin package

A package is a plugin if it has the app's custom NuGet `PackageType` (e.g. `MyAppPlugin`), which is also used to filter search results. It also contains a **plugin manifest**, `<PackageId>.plugin.json`, at the package root:

```json
{
  "schemaVersion": 1,
  "entryPoint": "Contoso.Charts.ChartsPlugin",
  "cell": "Contoso.Shared",
  "isolated": false,
  "privateDependencies": [ "Newtonsoft.Json" ]
}
```

Every field except `schemaVersion` is optional. Display metadata (title, description, icon, authors, project URL, license) always comes from the `.nuspec` ([ADR 0002](adr/0002-nuget-packages-packagetype-and-manifest.md)). `Alcove.Sdk` generates both the package type and the manifest from MSBuild properties.

When a plugin package's `.nuspec` depends on another package that is *also* a plugin package, that's a **plugin dependency**: the other package is installed and loaded as a plugin, not as a library.

## 4. Assembly classification

Every assembly a plugin might load falls into exactly one of these classes. The class decides which load context provides it ([ADR 0004](adr/0004-dependency-sharing-classes.md)).

| Class | What | Loaded from | Version conflict |
|---|---|---|---|
| **Host-owned** | .NET shared frameworks (including `Microsoft.WindowsDesktop.App`), `Alcove.Abstractions` and its dependencies (`NuGet.Versioning`, `Microsoft.Extensions.Logging.Abstractions`, and that package's dependency `Microsoft.Extensions.DependencyInjection.Abstractions`): **a dependency of a host-owned package is host-owned too**; packages the app lists in `HostOwnedPackages` (contracts, `<TheApp>.Sdk`) | Default context | Plugin is **incompatible** |
| **Plugin-provided** | Assemblies of any plugin in this cell's *transitive* plugin-dependency closure, **plus that plugin's exposed dependencies** | That plugin's cell | Exposed: `ExposedDependencyConflict`; otherwise NuGet resolution |
| **Host-shared** | Anything else in the host's `deps.json` | Default context | Becomes cell-private |
| **Cell-private** | Host-shared conflicts, dependencies the plugin developer marked private, and dependencies the host doesn't have | The plugin's cell | One version per cell |

"Conflict" follows NuGet semantics: the host's version doesn't satisfy the version range the plugin requests.

### Host inventory *(spike: nuget-resolution-and-assets)*

- `AppContext.GetData("APP_CONTEXT_DEPS_FILES")` (`;`-separated on every OS) lists the app's `deps.json` plus one `deps.json` per resolved shared framework, with the framework's name and exact version in the path. All are parsed with System.Text.Json (no `Microsoft.Extensions.DependencyModel`).
- From the app's `deps.json`: package ID → version, and assembly → package. Package version and `AssemblyVersion` often differ (e.g. Serilog 3.1.1 has `AssemblyVersion` 2.0.0.0), so both are kept.
- **Framework-provided packages** (e.g. a `System.Text.Json` package reference on .NET 10) are **skipped**, not version-checked, matching the SDK's pruning. The rule: package ID equals a framework assembly name, and package major ≤ framework major.
- **Host framework** for asset selection comes from the entry assembly's `TargetFrameworkAttribute` + `TargetPlatformAttribute`, **not** from `deps.json`, which drops the platform version (`net10.0-windows7.0` → `net10.0-windows`) and would select the wrong assets.
- **Single-file hosts expose no inventory** (no deps files, empty TPA list). They're unsupported in v1, and `PluginHost.Create` throws `UnsupportedHostException` (a `NotSupportedException`). See [open questions](open-questions.md#single-file-and-trimmed-hosts).

### Exposed dependencies *(spike: cross-compartment-deps)*

A plugin's **exposed dependencies** are the dependencies whose types appear in its public API. They're found at install time by scanning the plugin's assemblies with `System.Reflection.Metadata` (4–36 ms per assembly) and stored in the state file. Any cell using that plugin must resolve them **through the plugin's cell**: a separate copy, even an identical file, breaks type identity, sometimes silently (`is` checks return false). If a consumer needs a version the provider's cell doesn't satisfy, validation reports **`ExposedDependencyConflict`**. A silent duplicate is never loaded ([ADR 0014](adr/0014-cross-cell-dependencies.md)).

### WPF and duplicate assembly names *(spike: wpf-across-load-contexts; predicted from WPF source, confirmed on Windows 2026-10-19)*

WPF knows nothing about load contexts. It finds assemblies by scanning every loaded assembly in the process, matching by simple name (and `AssemblyVersion`/token only if the request names them), and **the most recently loaded match wins**. It also re-points its cached pack-URI lookups whenever another assembly with the same name loads. Consequences:

- **One copy per name works fully:** BAML, `pack://` URIs, merged dictionaries, cross-plugin XAML types, implicit `DataTemplate`s, `DynamicResource`.
- **Same identity** (name + `AssemblyVersion` + token), including identical copies of one package in two cells: the *earlier* copy's views **break** ("does not have a resource identified by the URI", `InvalidCastException`, templates not matching). Lookless controls' default styles from their own `Themes/Generic.xaml` are the exception: they keep working (confirmed on Windows).
- **Different `AssemblyVersion`:** mostly works, but name-only lookups go to the newest copy. These are unversioned `pack://` URIs, cross-assembly `ResourceDictionary.Source`, loose XAML, and `InitializeComponent` of assemblies built without an explicit `AssemblyVersion` property (the SDK default).
- What counts is the **`AssemblyVersion`, not the NuGet version**.
- A hot install that loads a duplicate can break plugins **that are already running**.
- WPF's fallback loads (`Assembly.Load` from PresentationFramework) bind in the **default** context and never ask a cell.

The library therefore:
- **Detects** duplicates statically at validation and hot install. An assembly is *WPF-relevant* if it contains `<AssemblyName>.g.resources` or is referenced by one that does, either through its AssemblyRef table or through assembly names recorded in its BAML. Compiled XAML that uses a library only from markup doesn't produce an AssemblyRef, so BAML has to be read too. A same-identity duplicate gives a "will break" warning, and a different `AssemblyVersion` gives a "may break" warning.
- Turns a same-identity duplicate at hot install into `PendingRestart`.
- Loads identical (same package ID + version) WPF-relevant private assemblies **once** and shares them across cells ([ADR 0015](adr/0015-share-identical-wpf-assemblies.md)). Other identical packages stay per-cell.
- Recommends listing WPF control libraries in `HostOwnedPackages` where possible.
- At runtime, `Alcove.Wpf` pre-loads WPF-relevant private assemblies and builds versioned pack URIs (§11).

## 5. Cells

A **cell** is one `AssemblyLoadContext` (non-collectible) plus one optional service provider (when a DI add-on is used). Assignment rules, applied in order:

1. A cell defined in the app's options that lists the plugin. **The app wins** over the plugin's own request.
2. A cell named in the plugin's manifest (`"cell": "X"`). Plugins naming the same cell share it.
3. Otherwise a default cell holding only this plugin.

A plugin's `isolated` flag always wins: if the app's options put an isolated plugin in a cell with anything else, that's a validation error, not a silent override.

Then validation checks:
- An **isolated** plugin must be alone in its cell; assigning anything else to it is an error.
- Each assembly appears in only one version per cell. The runtime doesn't enforce this for names answered from the host's context, so **the library enforces it** (see "Answer freezing" below).
- Within a cell, dependencies are resolved together ([ADR 0016](adr/0016-resolution-with-dependency-walker.md)) *(spike: nuget-resolution-and-assets)*:
  - **Walker:** NuGet's `RemoteDependencyWalker` (PackageReference semantics: nearest wins, lowest applicable version) from a synthetic root that depends on all the cell's plugins and **pins every host package** at the host's version.
  - **Host-owned downgrade:** the plugin is incompatible.
  - **Host-shared downgrade:** unpin, walk again, and the package becomes cell-private.
  - **Range conflicts** (`Analyze().VersionConflicts`) fail the install or update (`InstallStatus.Failed` / `ChangeStatus.Failed`, with reasons naming the packages and ranges). A warning is logged (event 3012) for diamonds that were resolved. At load, different versions recorded for one package in a cell give a `CellVersionConflict` warning, and the highest is used.
- **`ExposedDependencyConflict`:** a consumer needs a version of a provider's exposed dependency that the provider's cell doesn't satisfy.
- **WPF duplicate detection** (§4).
- Updating any plugin in a cell reloads the whole cell at next startup.

### Load-context resolution order

When a cell's `Load(AssemblyName)` is called:

*(spikes: cross-compartment-deps, hot-load-into-loaded-compartment)*

0. **Frozen answer:** if this name already has a successful answer in this cell, return the same answer again.
1. **Host-owned** (including shared frameworks) → return `null` (the runtime falls back to the default context).
2. A plugin in the cell's **transitive** plugin-dependency closure, or one of that plugin's **exposed dependencies** → `X.LoadFromAssemblyName(name)` on the providing cell X. Delegate this way rather than looking up what X has already loaded, because that depends on load order.
3. **Cell-private**, including the cell's own plugin assemblies → load from `packages/<id>/<version>/lib/<tfm>/`.
   - 3a. If an identical copy (same package ID and version) of a **WPF-relevant** assembly is already loaded in another cell, return that instance instead ([ADR 0015](adr/0015-share-identical-wpf-assemblies.md)).
4. **Host-shared** → `null` (default context).
5. **Unknown** → `null`, and log at debug level.

After steps 2–3, **check the version**: reject an assembly older than requested. The runtime accepts a lower version from a custom `Load`, and the failure only shows up later as a `MissingMethodException`.

### Answer freezing *(spike: hot-load-into-loaded-compartment)*

The runtime pins each reference once it's bound, but it does **not** remember a `null` ("use the host's copy") answer per cell. Every newly loaded assembly asks again. If the answer changed in between (for example after a hot install), one cell could end up with two copies of an assembly, with no error raised. Each cell therefore keeps:

- **Plan:** the planned answer for every assembly in its plugins' resolved dependency sets.
- **Given:** the first successful answer actually handed out for any name, including names outside the plan and native library paths. Failures are recorded (via `Resolving`) but never overwrite a success.
- **Committed** = Plan ∪ Given.

`Load` freezes answers under one lock, which is shared with the install pipeline. Failed resolutions aren't cached by the runtime, so a dependency that turns up later is picked up.

`LoadUnmanagedDll` resolves native libraries from `runtimes/<rid>/native/`, using the runtime identifier fallback graph. Satellite (localised resource) assemblies resolve from `lib/<tfm>/<culture>/`.

**Native library caveat** *(spike: hot-load-into-loaded-compartment)*: a native library's *own* dependencies are matched by file name (soname on Linux) against everything already loaded anywhere in the process. Two cells with different versions of the same native dependency can therefore end up sharing one, even after a restart. The library warns about soname collisions across cells. Windows does the same by module name (confirmed 2026-10-21: a DLL's import resolved to the same-named DLL another cell had loaded, not the one next to it).

Assets are selected with NuGet's `ManagedCodeConventions` + `ContentItemCollection.FindBestItemGroup`, and an embedded copy of the SDK's `PortableRuntimeIdentifierGraph.json`. This matched `dotnet build`'s `deps.json` in every case tested, including RID-specific and native assets *(spike: nuget-resolution-and-assets)*. The host framework comes from the entry assembly's attributes (§4). Only `lib/` and `runtimes/` are used; `content`, `contentFiles`, `build` and `tools` are ignored.

## 6. Flows

### Startup: `LoadAsync()`

```
read state
  └─▶ apply staged changes (takes .lock briefly): updates, uninstalls, enable/disable
       └─▶ validate (always; local only)
            └─▶ assign cells
                 └─▶ order plugins by dependency (topological; cycles already rejected)
                      └─▶ for each plugin:
                           create/reuse cell → load assemblies
                           → find entry point or [Extension] types
                           → Register() (collects registrations)
                           → InitializeAsync()
                           → catalog (only after InitializeAsync succeeds)
                           (on exception: mark Faulted, raise event, continue)
```

- **Cells are recomputed at every load** from the app's options and the manifests (§5). The cell recorded in the state file is informational.
- A faulted plugin contributes nothing to the catalog, and plugins depending on it aren't loaded (status `NotLoaded`, with the reason).
- **Validation** checks: state file vs. disk (each extracted file's size and timestamp; a full SHA-512 re-hash is available on demand through `VerifyAsync()`), **every recorded dependency version range** against the running host (the state file keeps the ranges each package declared, so a host that upgrades its contracts past a plugin's range makes that plugin incompatible: `HostOwnedConflict`; for host-shared packages: `HostSharedConflict`, which needs re-resolution), host-owned conflicts, cell rules, plugin dependency graph (present, no cycles), manifest schema version supported, entry point discovery (`EntryPointAmbiguous`), exposed-dependency conflicts, WPF duplicates and native library collisions. Validation never uses the network ([ADR 0006](adr/0006-validation-always-resolution-optional.md)).
- **Resolution** is a separate, optional call that the app can make before loading (`RestoreAsync()`, `CheckForUpdatesAsync()`, `UpdateAsync()`). Without it, startup is entirely offline.
- Disabled plugins are skipped. A plugin whose plugin dependency is faulted, disabled or missing is marked as not loaded, with that reason.
- If `FailFast` is set (for development), the first fault throws.
- **Development plugins** ([ADR 0017](adr/0017-development-plugins.md)), when the app listed some in `PluginHostOptions.DevelopmentPlugins`, join the plan right after the state file is read: each build output's manifest gives the ID and version, its `deps.json` the dependency set, classified as an install would against the host inventory (§4); its cell-private assets are planned from the build output, in place. One with an installed plugin's ID replaces it for this run (warning). Extra checks: host-owned references the host can't satisfy (error), dependencies the build resolved differently from an install (warning), unusable folders (error for that plugin). From there on (cells, order, activation) they're ordinary plugins, but they never reach the state file, the in-use record, cleanup, trust checks or the pending changes.

### Install: `InstallAsync(id, versionRange?)`

```
take .lock → re-read state.json (another instance may have changed it)
  └─▶ re-resolve the target cell(s) together with the new plugin (walker + host pins)
       ├─ host-owned conflict → fail: incompatible
       └─ host-shared conflict → re-walk, mark cell-private
  └─▶ trust policy: allowed feed? (before download)
  └─▶ download .nupkg → verify signature per policy (before extraction) → extract
       → record contentHash → scan exposed dependencies
  └─▶ validate the would-be state → write state.json → release .lock
  └─▶ hot-load rule, per name in the new resolution (see below)
       ├─ all OK → load now, dependencies first; catalog events fire in load order
       └─ otherwise → PendingRestart, listing the conflicting names
```

**Hot-load rule** *(spike: hot-load-into-loaded-compartment)*. Each name in the new resolution is checked against the target cell's **committed** answers. It's **OK** if:
- the cell has never answered it, or
- it previously only failed, or
- the new answer is identical (same source and package version; for native libraries, the same path).

Anything else means `PendingRestart`. Other reasons for `PendingRestart`:
- the install would change a provider cell's exposed dependency;
- it would create a same-identity duplicate of a WPF-relevant assembly (§4).
- it registers services (`IServicePlugin`) but its cell's service provider is already built (§10).

A plugin going into a **new** cell always hot-loads, unless one of those reasons applies.

Prerelease versions are only considered when allowed (per call or in options).

**Install serialisation:** one install at a time per host covers resolve → state write → hot-load decision and plan merge. Activation (plugin code) runs after that, so a plugin may itself call `InstallAsync` from `InitializeAsync`, directly or through an app worker. An install's activation waits only for earlier activations it depends on (those providing its plugin dependencies, or loading into a cell it joins); unrelated installs don't wait for each other. A nested install that depends on a plugin still initialising is `PendingRestart`. An install's status is `Faulted` only if one of its plugins faulted; plugins that end up not loaded for other reasons (cancellation, dispose, a dependency's activation cancelled) make it `PendingRestart` and are listed in pending changes. Cancelling after the state write returns a result rather than throwing. An install that races `LoadAsync` may report `PendingRestart` even if `LoadAsync` loaded it; `Plugins` and pending changes are corrected when `LoadAsync` finishes.

**Trust for reused packages:** packages already in `packages/` that a new or changed plan uses (including kept versions for rollback) are re-verified against the current trust policy from their kept `.nupkg`. The state file records each package's source feed (falling back to `.nupkg.metadata` for older entries), so `AllowedFeeds` applies to reused packages too: an unknown source is rejected (`UnknownSource`) when `AllowedFeeds` is set, with a reason suggesting a reinstall from an allowed feed. Signature acceptances are cached for the host's lifetime with `Revocation = Offline`, and for 10 minutes with `Online`. Downloads are always verified.

An install is resolved against the state the **next startup** will load: plugins in the target cell with staged version switches are re-resolved with their staged versions, and their staged targets are updated. If the result only works with staged versions this process doesn't run yet, the install is `PendingRestart` with that reason. Installing a plugin that is staged for uninstall cancels the uninstall: the same version gives `Installed` with a reason, a different version becomes a staged switch (`PendingRestart`).

### Update, uninstall, enable/disable

These record a **staged change** in `state.json`; nothing loaded changes. An update downloads and verifies the new version immediately, so the next startup needs no network. Updating a plugin also re-resolves its cell and the cells of plugins that depend on it.

Staged changes are computed against the state the **next** startup would load, so they combine (rolling back a staged update cancels it). Each operation returns a `ChangeResult`.

**At startup** `LoadAsync` takes the root lock, applies staged changes and trims previous versions to `VersionsToKeep`. If another process holds the lock past the timeout, it logs a warning and loads the plugins as they are; the changes apply at a later start. **After loading** (even if some plugins faulted), it deletes every folder in `packages/` that the state doesn't reference, that this process didn't load from, and that no **live in-use record** lists. Every running host writes its record to `<root>/.inuse/` (process ID + start time, so a reused PID isn't mistaken for a live one) at startup, before validation and before any assembly loads, while holding the root lock (if the lock is busy, right after reading the state, which leaves a tiny window), and after each hot install. It deletes the record on dispose. Records of dead processes are removed; an unreadable record of a live process blocks cleanup until that process exits. Folders are first moved to `packages/.deleted/`; a folder that can't be deleted (e.g. locked on Windows) is retried at the next start. Uninstalling keeps `data/<id>`.

- Uninstalling a plugin that others depend on is **blocked** unless `cascade: true` is passed.
- `RollbackAsync(pluginId)` stages a switch back to a kept previous version. The library never rolls back by itself ([ADR 0011](adr/0011-report-failures-never-self-heal.md)).

### Pending changes

`GetPendingChangesAsync()` compares what this process has loaded with the current `state.json`, for example after another instance installed something. It returns a list of differences that will apply at next startup. This includes `PendingRestart` installs (with their reasons), and Faulted plugins whose missing dependency has since been installed. Those are never re-activated during the run ([ADR 0011](adr/0011-report-failures-never-self-heal.md)). There is no file watcher or "changed" event.

### Restore

`RestoreAsync()` downloads every package `state.json` pins (exact version and hash) that's missing from `packages/`, e.g. on a fresh machine with only `config.json` and `state.json` copied over.

## 7. Plugin-facing runtime model

```csharp
public interface IPlugin
{
    void Register(IExtensionRegistrar registrar, IPluginContext context);
    ValueTask InitializeAsync(IPluginContext context, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;
}

public interface IPluginContext
{
    string PluginId { get; }
    NuGetVersion Version { get; }       // NuGet.Versioning, already a dependency of the core
    string DataDirectory { get; }
    ILogger Logger { get; }              // Microsoft.Extensions.Logging.Abstractions (host-owned; see §1)
    IServiceProvider Services { get; }   // host services (+ cell services with a DI add-on)
}
```

- **Entry point optional.** The manifest's `entryPoint` wins. Otherwise, if the plugin's own assemblies contain exactly **one** public, non-abstract class implementing `IPlugin` (found from metadata), that's the entry point; several is a validation error (`EntryPointAmbiguous`) asking the author to set `PluginEntryPoint`. A plugin **with** an entry point (declared or discovered) is not scanned for attributes: it registers its extensions itself. A plugin **without** one is scanned for types with `[Extension]` attributes only; implementing an extension-point interface alone is never enough.
- **Metadata through attributes.** An app can subclass `ExtensionAttribute` (e.g. `[ToolWindow(Title = "Charts")]`). The attribute instance becomes that extension's metadata, readable without creating the extension.
- **Activator.** Extensions registered by type are created through a replaceable `IExtensionActivator`. The default uses the registered factory or a public parameterless constructor. DI add-ons replace it.

## 8. The extension catalog

```csharp
// Alcove.Catalog.ExtensionCatalog
IReadOnlyList<Extension<T>>            GetAll<T>();
IReadOnlyList<Extension<T, TMetadata>> GetAll<T, TMetadata>();
IDisposable Observe<T>(Action<Extension<T>> onAdded);   // existing ones, then new ones
event EventHandler<ExtensionsAddedEventArgs> ExtensionsAdded;
```

`Extension<T>` exposes `Plugin` (ID, version, cell), `Metadata`, `Lifetime`, and the instance. `GetInstance()` returns the right thing for either lifetime; `Value` and `CreateInstance()` are strict and throw `InvalidOperationException` when used with the other lifetime. **Each registration chooses its lifetime:**

- `Singleton` (default): one instance, created on first use; `Value` returns it.
- `Transient`: a new instance per request; `CreateInstance()` returns one. Useful for windows that can be open more than once.

```csharp
registrar.Add<IToolWindow>(() => new ChartWindow(), metadata, ExtensionLifetime.Transient);
```

For attribute-discovered extensions, `ExtensionAttribute` has a `Lifetime` property.

There is no "removed" event during a run: nothing is removed until restart ([ADR 0007](adr/0007-extension-catalog.md)).

## 9. Threading

- **Dispose:** `DisposeAsync` cancels in-flight `LoadAsync` and install activations, waits for them (including plugins that ignore cancellation, so a misbehaving plugin can delay it), then disposes every activated plugin, extension and cell provider in reverse load order; no plugin activates after dispose has started. A second call returns the same task. Called from plugin code of an operation it would wait for, it doesn't wait for that operation; anything that operation creates afterwards is disposed when it ends. `LoadAsync` interrupted by dispose throws `OperationCanceledException` and skips cleanup. A singleton whose creation finishes during dispose is disposed at once (`Value` throws `ObjectDisposedException`).
- **Init token:** the token passed to `InitializeAsync` follows the app's token (passed to `LoadAsync` / `InstallAsync`) **during the call**, and afterwards only the host's disposal, so plugins may tie background work to it without a call timeout cancelling that work later.
- **Callbacks:** `PluginFaulted`, `ExtensionsAdded` and `Observe` callbacks are never raised under the library's locks, and logging never runs under the cell gate. Singleton extension constructors do run under their registration's own lock, so a constructor shouldn't block on another thread that resolves the same extension.

- Catalog events fire on the thread that performed the install. In practice that's whatever thread the app's `await InstallAsync` continued on.
- **`Alcove.Wpf` dispatcher policy** (§11):
  - **Catalog callbacks:** `DispatcherExtensionCatalog` runs an `Observe` or `ExtensionsAdded` callback **inline** when it's raised on the UI thread. So `Observe` called on the UI thread has delivered the existing extensions when it returns. A callback raised on another thread (e.g. a hot install that continued on the thread pool) is queued with `Dispatcher.BeginInvoke`, so the loading thread never waits for the UI thread. Each subscription keeps its order, and nothing is delivered after the subscription is disposed. An exception from a queued callback surfaces on the dispatcher (`Dispatcher.UnhandledException`).
  - **Resource merging and removal:** these run on the dispatcher's thread, inline when the host loads there, otherwise with a blocking `Dispatcher.Invoke`. Plugins' resources must be in place before their extensions can be created. This can deadlock only if the UI thread blocks synchronously on the host (`.Wait()` / `.Result` on `LoadAsync`, `InstallAsync` or `DisposeAsync`) while another thread loads. Awaiting them is safe.
- `InitializeAsync` calls are awaited in dependency order. The core doesn't move work to the thread pool, so a WPF app that awaits `LoadAsync` from the UI thread keeps plugin initialisation on the UI thread unless plugins themselves move off it.
- All async APIs take a `CancellationToken`. Install, update and restore accept an `IProgress<InstallProgress>` (step, package ID, version).

## 10. Dependency injection add-ons

([ADR 0008](adr/0008-di-agnostic-core-with-addons.md))

- **Core extension point:** `ICellServicesFactory` (a code-only option). The core asks it to create one `IServiceProvider` per cell, before the cell's plugins register. That provider is used for `IPluginContext.Services` and extension activation, and is disposed after the cell's extensions and plugins. The core doesn't know any container.
- **Host side:** `services.AddPlugins(options => ...)` (Microsoft DI) or `builder.AddPlugins(...)` (Autofac) registers `PluginHost` and `ExtensionCatalog`, installs the cell services factory, and sets an activator based on the container (e.g. `ActivatorUtilities`; unless the app set one) so extensions get constructor injection. Apps that create the host themselves call `options.UseDependencyInjection(serviceProvider)` or `options.UseAutofac(lifetimeScope)` instead. `AddPlugins` defaults `LoggerFactory` to the container's `ILoggerFactory`. The app calls `PluginHost.LoadAsync` itself, and disposes the container with `DisposeAsync`.
- **Host-owned:** the add-on package is added to the host-owned set automatically, and the add-ons' buildTransitive props mark it host-owned for plugin projects.
- **Plugin side:** a plugin may implement `IServicePlugin` (Microsoft DI: `ConfigureServices(IServiceCollection)`; Autofac: `ConfigureContainer(ContainerBuilder)`). Each **cell** gets one service provider holding the registrations of all its plugins, falling back to the host's provider.
- Extensions are **never** added to the root container's `IEnumerable<T>`. The host gets extensions from the catalog, because the root container is fixed after it's built and hot installs would be invisible.
- Cell providers are disposed in reverse load order when the host is disposed.
- Other containers: adapt them through `IExtensionActivator` + `IServiceProvider`. Only Microsoft DI and Autofac adapters are shipped.

## 11. WPF add-on (`Alcove.Wpf`)

WPF knows nothing about load contexts (§4): it finds assemblies by simple name among everything loaded in the process, and its own fallback loads go to the default context, never to a cell. `Alcove.Wpf` (`net10.0-windows`) makes plugins' XAML work inside cells. It's set up with one call, `options.UseWpf(application)` (or `UseWpf(dispatcher)`), which adds a cell observer and makes `Alcove.Wpf` host-owned. Calling it twice on the same options throws. *(spike: wpf-across-load-contexts; the spike's predictions were confirmed on Windows 2026-10-19, and the add-on's own scenarios run in the Windows CI job)*

### The core hook: cell observers

The core knows no UI framework. It tells **cell observers** (`ICellObserver`) about cells, and UI add-ons plug in there:

- `PluginHostOptions.CellObservers` is a code-only list of `CellObserverFactory` delegates (`ILoggerFactory → ICellObserver`). Each host calls every factory once, with its logger factory, so an observer only ever sees **one host's** cells. A factory that returns `null` makes `PluginHost.Create` throw.
- `CellCreated(CellInfo)`: a cell was created, and nothing has loaded into it yet.
- `CellExtended(CellInfo, added)`: a hot install added private assemblies to a cell that already exists, before they load. It isn't called for cells an install creates, which get `CellCreated`.
- `PluginLoading(CellInfo, PluginInfo, assemblies)`: the plugin's own assemblies are loaded and its entry point is created, and the plugin is about to register its extensions. Runs inside the cell's contextual reflection scope.
- `CellInfo` gives the cell's name, its `AssemblyLoadContext`, its plugins and its planned private assemblies (`CellAssembly`: name, path, package ID and version, and `IsWpfRelevant`). Planned doesn't mean loaded.
- Observers are called on the thread that loads or installs, never under the library's locks, so they may load assemblies into the cell. An exception from `CellCreated` or `CellExtended` is logged (2009) and doesn't stop the load. One from `PluginLoading` faults that plugin.
- **Lifetime:** when the host is disposed, after its plugins and cell service providers, it disposes the observers that implement `IAsyncDisposable` or `IDisposable`, in reverse order, on the thread that disposes the host. A failure there is logged (2010).

### What `UseWpf` does

- **Pre-loading** (`PreloadWpfAssemblies`, on by default). When a cell is created, every **WPF-relevant** cell-private assembly in its plan is loaded through the cell, and when a hot install extends a cell, the ones it adds are loaded too. WPF only finds assemblies that are already loaded, so a control library used only from XAML would otherwise not be found. Loading through the cell means the cell's answers apply, including sharing identical copies across cells ([ADR 0015](adr/0015-share-identical-wpf-assemblies.md)). A failed pre-load is logged (6001) and doesn't stop the others.
- **The `Default.Resolving` forwarder** (`ForwardDefaultContextLoads`, opt-in). It's registered on `AssemblyLoadContext.Default.Resolving` when the host is created, and removed when the host disposes its observer, so it lives exactly as long as the host. A failed default-context load is forwarded to the **single** cell of that host that owns a private copy with that simple name. The version is ignored, though the cell still rejects a request for a higher version than its copy. This covers WPF's own fallback loads: a XAML library rolled forward to a newer version in its cell, and external theme assemblies. Identical WPF-relevant copies (same package ID and version) count as one owner, because the cells share them. If several cells own different copies, the forwarder doesn't choose; it logs a warning (6003) and returns nothing, since any choice would give the other cells' XAML a foreign copy. Satellite assemblies aren't forwarded. The forwarder is process-wide by nature, so it's the documented opt-in exception in [ADR 0012](adr/0012-app-supplies-root-no-global-state.md).
- **No WPF activator.** The core already creates every extension inside `cell.EnterContextualReflection()`, whichever activator is set (including those of `Alcove.DependencyInjection` and `Alcove.Autofac`). So `UseWpf` sets no activator and combines with the DI add-ons in either order. Contextual reflection alone isn't enough, because templates expand later; pre-loading and versioned URIs cover the rest.
- **Resource merging** (`MergePluginResources`, on by default). When a plugin loads, before it registers, its assemblies' dictionaries are merged into `WpfPluginOptions.Resources`. That defaults to the application's resources (the `Application` passed to `UseWpf`, or `Application.Current` when a dispatcher was passed). The dictionaries are those named by `[assembly: PluginResources("...")]` (in the order declared; a missing one faults the plugin), or `Themes/Plugin.xaml` if the assembly declares none and has that part. Each plugin gets **one container dictionary**, holding its dictionaries (loaded with versioned pack URIs) and its view templates. The container is inserted into `MergedDictionaries` **below the app's own dictionaries** and after earlier plugins' containers. WPF searches merged dictionaries last-first, so the app's resources win over every plugin's. The containers are removed again when the host is disposed (skipped if the dispatcher is already shutting down). If there is no target (no `Application` and no `Resources`), the plugin's resources aren't merged and a warning is logged (6006).
- **View-model to view mapping** (`MapViewModelsToViews`, opt-in). When a plugin loads, each public `FooViewModel` gets an implicit `DataTemplate` showing `FooView`. The view must be in the same namespace, or in the namespace with its `ViewModels` segment replaced by `Views`, and in the same assembly. The templates go into the plugin's container dictionary. `ViewTemplates.Create` and `ViewTemplates.CreateForAssembly` expose the same convention for manual use.
- **AppContext switch.** The host should set `Switch.System.Windows.Baml2006.AppendLocalAssemblyVersionForSourceUri=true` (a `RuntimeHostConfigurationOption` in its project file), so XAML that refers to its own assembly's dictionaries gets a versioned URI. If it isn't set, the observer logs an Information message (6009) when the host is created.

### Helpers

- **`DispatcherExtensionCatalog`** (`host.Catalog.OnDispatcher(dispatcher)`): the catalog with `Observe` and `ExtensionsAdded` callbacks on the UI thread (§9). `GetAll` passes through.
- **`PluginPackUri`**: `Create`/`Format` give `/Name;v1.2.3.4;component/path` (plus the public key token if strong-named), and `CreateAbsolute` gives the `pack://application:,,,` form. The URIs **always name the loaded assembly's version**, so they can't reach another cell's copy. Resource merging uses them.
- **`SharedResourceKey<T>`**: a typed resource key the app defines once as a constant in its contracts package or app SDK (host-owned, so every cell sees the same key object). Keys are equal by ID and value type. Use them from XAML as `{DynamicResource {x:Static sdk:AppKeys.Accent}}`, or from code with `SetResource`, `SetResourceReference` and `TryFindResource`.
- **`[assembly: PluginResources("Themes/Colors.xaml")]`**: names the dictionaries a plugin assembly wants merged. It's read by name from metadata, so no type identity is needed.
- **`buildTransitive`**: a plugin project that references `Alcove.Wpf` together with `Alcove.Sdk` treats it as host-owned automatically (`PluginHostOwnedPackage`).

Logging uses the `Alcove.Wpf` category, event IDs 6000–6099 ([logging.md](logging.md)).

App-provided UI building blocks (`<TheApp>.Sdk` with UserControls, styles and shared keys) are **host-owned assemblies**, so they're shared with all plugins automatically. Listing third-party WPF control libraries as host-owned too avoids the duplicate problem entirely. Docking, menus and ribbons are deliberately left out; the app defines those as its own extension points.

## 12. Feeds, authentication and trust

- Feeds come from `PluginHostOptions.Feeds`, and optionally from a `nuget.config` the app points to.
- Authentication: credentials on a `PackageFeed`, credentials from the `nuget.config` the app points to, default network credentials, and an app-supplied `ICredentialProvider` (`GetCredentialsAsync(feedUri, isRetry, ct)`, e.g. a WPF login dialog). NuGet's *plugin* credential providers (e.g. Azure Artifacts') aren't supported in v1.
- **Trust policy** (optional) *(spike: nuget-signature-verification)*:
  ```csharp
  // namespace Alcove.Trust
  public sealed class TrustPolicy
  {
      public IList<string> AllowedFeeds { get; init; } = [];    // checked before download; empty = any configured feed
      public bool RequireSignedPackages { get; set; }          // plugins AND their dependencies
      public IList<AllowedSigner> AllowedSigners { get; init; } = [];   // empty = any trusted signer
      public RevocationCheck Revocation { get; set; } = RevocationCheck.Online;    // or Offline
  }
  public sealed record AllowedSigner
  {
      public SignerKind Kind { get; init; }              // Author | Repository
      public string Fingerprint { get; init; } = "";     // SHA-256 of the leaf signing certificate
      public IList<string> Owners { get; init; } = [];   // Repository only: package must list >= 1 of these
      public bool AllowUntrustedRoot { get; init; }      // self-signed / private CA, no certificate store changes
      public string? Name { get; init; }                 // display name for logs and messages
      public static IEnumerable<AllowedSigner> NuGetOrg(params string[] owners);  // nuget.org's rotating repo certs (2018, 2021, 2024; refresh when rotated)
  }
  ```
  - Signed packages are **always** integrity-checked, even when signatures aren't required. A tampered signed package always fails.
  - Verification uses NuGet's `PackageSignatureVerifier` (integrity, trust and validity, allow-list providers), runs on the `.nupkg` after download and before extraction, and reports its own failure reasons. NuGet's messages aren't reliable enough on their own.
  - **Root certificates:** on Linux the default .NET store rejects most nuget.org packages. The library therefore ships the SDK's code-signing and timestamping root bundle next to `NuGet.Packaging.dll` and calls `X509TrustStore.InitializeForDotNetSdk()` on every OS. That call is process-wide (ADR 0012).
  - **Revocation:** `Online` is NuGet's default. A network that hangs (rather than refusing) costs **about 120 s per package**, and a `CancellationToken` doesn't stop it. `Offline` never touches the network. **The default is `Online`** (NuGet parity). Hosts on air-gapped or unreliable networks should choose `Offline`. Verification runs off the UI thread and reports a progress step.
- **Hash pinning** (always on): each package's `contentHash` is stored at install, along with each extracted file's size and timestamp. Startup validation checks sizes and timestamps. `VerifyAsync()` re-hashes everything on demand, and can re-verify signatures of the kept `.nupkg` files.
- Plugins run with full trust ([ADR 0010](adr/0010-trust-model.md)).

## 13. Configuration

([ADR 0013](adr/0013-own-json-configuration.md))

**Whatever options object the app passes to `PluginHost.Create` is the configuration.** The library never reads a configuration file on its own and never merges sources, so there are no precedence rules. The app chooses one of three patterns:

```csharp
// 1. Load the file as-is
var options = PluginHostOptions.Load(Path.Combine(root, "config.json"));

// 2. Load the file, then modify it
var options = PluginHostOptions.Load(Path.Combine(root, "config.json"));
options.TrustPolicy.RequireSignedPackages = true;   // e.g. enforce what operators can't relax

// 3. Build it entirely in code
var options = new PluginHostOptions { Root = root, ... };

var host = PluginHost.Create(options);
```

- The file format is plain JSON (System.Text.Json, camelCase, comments and trailing commas allowed, enums as strings) and covers every serialisable setting: root, feeds (without credentials), `nuGetConfigPath`, cells, trust policy, host-owned packages, package type, prerelease, versions to keep, `failFast`. Code-only settings (`LoggerFactory`, `Services`, `Activator`, `CellServicesFactory`, `CredentialProvider`, `PackageFeed.Credentials`) can't come from a file.
- `PluginHostOptions.Save(path)` writes the format back (atomically), so an app's settings UI can edit it.
- A relative `root` or `nuGetConfigPath` in the file is resolved against the file's own directory, so `Save` writes it back as an absolute path.
- Scalar options and `TrustPolicy` are `{ get; set; }`, and the collections (feeds, cells, host-owned packages, allowed feeds and signers) are mutable lists, so pattern 2 (load, then modify) works for nested settings too.
- Whether operators may change trust settings is entirely the app's choice; pattern 2 lets it override anything the file says.
- Binding from `Microsoft.Extensions.Configuration` can come later as an add-on; the core doesn't depend on it.

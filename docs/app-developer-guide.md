# App developer guide

This guide is for people building a **host app** that loads plugins.

**Namespaces.** The entry points are in `Alcove`: `PluginHost`, `PluginHostOptions`, `PluginInfo`, `PluginStatus`, `PluginException` and `PluginFaultedEventArgs`, plus the plugin contracts (`IPlugin`, `ExtensionAttribute`, …). Everything else is in the namespace of its feature:

| Namespace | Types |
|---|---|
| `Alcove.Catalog` | `ExtensionCatalog`, `Extension<T>`, `ExtensionsAddedEventArgs`, `IExtensionActivator` |
| `Alcove.Cells` | `CellDefinition`, `ICellObserver`, `CellInfo`, `ICellServicesFactory` |
| `Alcove.Feeds` | `PackageFeed`, `ICredentialProvider`, `SearchOptions`, `PluginSearchResult` |
| `Alcove.Install` | `InstallResult`, `InstallStatus`, `InstallProgress` |
| `Alcove.Lifecycle` | `ChangeResult`, `PendingChange`, `PluginUpdate`, `RestoreResult`, `VerificationReport` |
| `Alcove.Trust` | `TrustPolicy`, `AllowedSigner`, `SignerKind`, `RevocationCheck`, `TrustIssueCodes` |
| `Alcove.Validation` | `ValidationReport`, `ValidationIssue`, `ValidationCodes` |
| `Alcove.Hosting` | `UnsupportedHostException` |
| `Alcove.Packaging` | `PluginManifest` |

The add-ons put their entry points in `Alcove.DependencyInjection`, `Alcove.Autofac` and `Alcove.Wpf`; `Alcove.Wpf` also has `Alcove.Wpf.Resources` (`SharedResourceKey<T>`, `PluginPackUri`), `Alcove.Wpf.Views` (`ViewTemplates`) and `Alcove.Wpf.Threading` (`OnDispatcher`).

## 1. Define your extension points

Create a contracts package (e.g. `MyApp.Contracts`) holding the interfaces plugins implement, plus any metadata attributes:

```csharp
using Alcove;

public interface IToolWindow
{
    object CreateContent();   // e.g. a UserControl
}

public sealed class ToolWindowAttribute : ExtensionAttribute
{
    public ToolWindowAttribute() : base(typeof(IToolWindow)) { }
    public required string Title { get; init; }
    public string? Icon { get; init; }
}
```

Publish it to your feed. Its version is how compatibility is expressed: a plugin built against `MyApp.Contracts [2.0, 3.0)` loads into any host shipping 2.x. Use semantic versioning strictly. A breaking change to an extension point needs a new major version.

You can also publish an app SDK (e.g. `MyApp.Sdk`) with UserControls, styles and helpers plugins can reuse. List both as **host-owned** so they're always shared, never loaded twice.

Optionally, ship a `build/MyApp.Contracts.props` in the contracts package, so plugin developers can't get the package type wrong and never copy your contracts into their output:

```xml
<Project>
  <PropertyGroup>
    <PluginPackageType Condition="'$(PluginPackageType)' == ''">MyAppPlugin</PluginPackageType>
  </PropertyGroup>
  <ItemGroup>
    <PluginHostOwnedPackage Include="MyApp.Contracts" />
    <PluginHostOwnedPackage Include="MyApp.Sdk" />
  </ItemGroup>
</Project>
```

## 2. Create and load the host

The options object you pass in is the whole configuration; the library never reads a config file by itself. Build it in code, or load it from a file (and optionally modify it):

```csharp
using Alcove;

var options = PluginHostOptions.Load(Path.Combine(root, "config.json"));
options.TrustPolicy.RequireSignedPackages = true;   // your rules override the file
var host = PluginHost.Create(options);
```

A configuration file looks like this (camelCase; comments and trailing commas are allowed; a relative `root` or `nuGetConfigPath` is relative to the file). `PluginHostOptions.Save(path)` writes it back atomically:

```json
{
  "root": "plugins",
  "pluginPackageType": "MyAppPlugin",
  "hostOwnedPackages": [ "MyApp.Contracts", "MyApp.Sdk" ],
  "feeds": [ { "source": "https://nuget.example.com/v3/index.json", "name": "Company feed" } ],
  "cells": [ { "name": "Reporting", "plugins": [ "Contoso.Charts", "Contoso.Tables" ] } ],
  "allowPrerelease": false,
  "versionsToKeep": 1,
  "trustPolicy": { "requireSignedPackages": true, "revocation": "Offline" }
}
```

In code, `Root` must be an absolute path; `PluginHost.Create` throws `ArgumentException` otherwise. Other options: `AllowPrerelease`, `VersionsToKeep` (previous versions kept for rollback, default 1), `FailFast` (development: the first plugin fault throws), `NuGetConfigPath` (an extra `nuget.config` whose sources and credentials are used; the user's NuGet settings are never read), and the code-only `LoggerFactory`, `Services` (offered to plugins as `IPluginContext.Services`), `CredentialProvider`, `Activator` and `CellServicesFactory`.

Or entirely in code:

```csharp
using Alcove;
using Alcove.Feeds;

var host = PluginHost.Create(new PluginHostOptions
{
    Root = Path.Combine(Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData), "MyApp", "plugins"),
    PluginPackageType = "MyAppPlugin",
    HostOwnedPackages = { "MyApp.Contracts", "MyApp.Sdk" },
    Feeds = { new PackageFeed("https://nuget.example.com/v3/index.json") },
    LoggerFactory = loggerFactory,
});

// Optional: bring local packages in line with the state file before loading.
await host.RestoreAsync(progress: restoreProgress, cancellationToken: ct);

// Optional: check what validation thinks before loading.
var report = await host.ValidateAsync(ct);

// Applies staged changes, always validates, then loads every enabled plugin. Call it once per host.
await host.LoadAsync(ct);
```

`LoadAsync` never contacts a feed. It throws `PluginException` (with the `ValidationReport` in `Report`) only for a global validation error, such as an unreadable state file. Other errors only stop the plugins concerned and the plugins that depend on them. `ValidateAsync` checks the plugin root as it is on disk; staged changes are applied only by `LoadAsync`. A report has `HasErrors`, `Errors`, `Warnings` and `ForPlugin(id)`, and each `ValidationIssue` has a stable `Code` from `ValidationCodes`.

A fault in one plugin doesn't stop the others. Watch `host.PluginFaulted` (raised at load, or later when an extension can't be created), or inspect `host.Plugins`:

```csharp
foreach (var plugin in host.Plugins.Where(p => p.Status == PluginStatus.Faulted))
    log.LogError(plugin.Fault, "Plugin {Id} failed to load", plugin.Id);
```

The library never disables a faulted plugin by itself. If you want "disable after crash" behaviour, call `host.DisableAsync(id)` yourself.

## 3. Use extensions

```csharp
foreach (var ext in host.Catalog.GetAll<IToolWindow, ToolWindowAttribute>())
    menu.Add(ext.Metadata.Title, () => Show(ext.GetInstance()));   // metadata without instantiation;
                                                                   // GetInstance() works for singletons and transients

// Existing extensions, then any added later by a hot install:
var subscription = host.Catalog.Observe<IToolWindow>(ext => menu.Add(...));
```

Creating an extension can throw `PluginException` if the plugin's factory or constructor throws; the plugin is then `Faulted`. `Value` only works for singletons and `CreateInstance()` only for transients; the other throws `InvalidOperationException`. `host.Catalog.ExtensionsAdded` is raised once per plugin whose extensions arrive.

Catalog callbacks run on the thread that loaded the plugin (the caller's context of `LoadAsync` / `InstallAsync`). In a WPF app, use `host.Catalog.OnDispatcher(dispatcher)` to get them on the UI thread ([§7](#7-wpf)).

## 4. Manage plugins

```csharp
using Alcove.Install;

var results = await host.SearchAsync("charts", ct);        // flags incompatible results, doesn't hide them
var versions = await host.GetVersionsAsync("Contoso.Charts", ct);
var updates  = await host.CheckForUpdatesAsync(ct);

var result = await host.InstallAsync("Contoso.Charts", progress: p, cancellationToken: ct);
if (result.Status == InstallStatus.PendingRestart)
    ShowRestartBanner(result.Reason);

await host.UpdateAsync("Contoso.Charts", ct);              // staged until restart
await host.UninstallAsync("Contoso.Charts", cascade: false, ct);
await host.DisableAsync("Contoso.Charts", ct);             // staged until restart
await host.RollbackAsync("Contoso.Charts", ct);            // staged until restart

var pending = await host.GetPendingChangesAsync(ct);       // includes changes from other instances
```

`SearchAsync(query, new SearchOptions { Skip = 0, Take = 20, IncludePrerelease = true }, ct)` pages results. Each `PluginSearchResult` has `Id`, `Version`, `Title`, `Description`, `Authors`, `IconUrl`, `ProjectUrl`, `Source`, `IsCompatible` and `IncompatibilityReason`. `InstallAsync` and `UpdateAsync` also take a `VersionRange` and `includePrerelease`. `progress` receives `InstallProgress` records (`Step`: `Resolving`, `Downloading`, `VerifyingSignatures`, `Extracting`, `Recording`, `Loading`). Installs and staged changes wait up to 30 seconds for another process's root lock, then throw `TimeoutException`.

`InstallResult.Status` is one of:

| Status | Meaning |
|---|---|
| `Loaded` | Installed and loaded now; its extensions are in the catalog. |
| `PendingRestart` | Installed, but it can't load into this process; it loads at next startup. `Reasons` / `ConflictingNames` say why. |
| `Installed` | Recorded but not loaded, because `LoadAsync` hasn't run yet (or it was already installed). |
| `Faulted` | Installed, but the plugin threw while loading. |
| `Incompatible` | Not installed: it needs a host-owned package version this host doesn't have. |
| `Failed` | Not installed: resolution, download or trust verification failed. `Issues` has the details. |

The restart-only operations (`UpdateAsync`, `UninstallAsync`, `EnableAsync`, `DisableAsync`, `RollbackAsync`) return a `ChangeResult`:

| Status | Meaning |
|---|---|
| `Staged` | Recorded; applies at next startup. |
| `Unchanged` | Nothing to do (e.g. already on the newest version). |
| `Blocked` | Refused, e.g. uninstalling a plugin others depend on without `cascade`. `Reasons` says why. |
| `Incompatible` | The update needs a host-owned version this host doesn't have. |
| `Failed` | Not staged: the plugin isn't installed, no usable version was found (for a rollback: no previous version is kept), a feed failed, the trust policy rejected a package, or validation of the new state failed. `Reasons` and `Issues` have the details. |

Staged changes combine: they're always relative to what the *next* startup will load. For example, rolling back a staged update cancels it.

`GetPendingChangesAsync` returns `PendingChange` entries (`Install`, `Update`, `Rollback`, `Uninstall`, `Enable`, `Disable`, `PendingRestart`, `WillLoad`). `CheckForUpdatesAsync` returns a `PluginUpdate` per plugin: the newest compatible version, plus newer incompatible ones with reasons. `RestoreAsync` returns a `RestoreResult`, and `VerifyAsync` a `VerificationReport` (pass `verifySignatures: true` to also re-check kept packages' signatures).

Uninstalling keeps the plugin's data directory (`<root>/data/<id>`), so reinstalling finds its data again.

Who may call these (end users, admins only, a config script) is up to you.

## 5. Cells

By default every plugin gets its own cell. To make plugins share one:

```csharp
using Alcove.Cells;

options.Cells.Add(new CellDefinition("Reporting")
{
    Plugins = { "Contoso.Charts", "Contoso.Tables" }
});
```

…or the same in your configuration file. Plugins can also ask for a named cell in their manifest; if your options say otherwise, yours win. Plugins can also demand isolation, which you can't override: putting an isolated plugin with others is a validation error.

## 6. Dependency injection (optional)

```csharp
// Microsoft.Extensions.DependencyInjection (package Alcove.DependencyInjection)
using Alcove;
using Alcove.Catalog;
using Alcove.DependencyInjection;

services.AddPlugins(options => { /* same options */ });
await using var provider = services.BuildServiceProvider();

await provider.GetRequiredService<PluginHost>().LoadAsync(ct);   // you decide when plugins load
var catalog = provider.GetRequiredService<ExtensionCatalog>();
```

```csharp
// Autofac (package Alcove.Autofac)
using Alcove;
using Alcove.Autofac;

builder.AddPlugins(options => { /* same options */ });
await using var container = builder.Build();
await container.Resolve<PluginHost>().LoadAsync(ct);
```

- Get extensions from the catalog, **not** from `IEnumerable<IToolWindow>` in your container. A hot-installed plugin can't change an already-built container. Extensions created through the catalog get constructor injection from your container plus their cell's services.
- Each cell gets its own child container holding the services its plugins register (`IServicePlugin`), falling back to yours. A single service comes from the cell if a plugin there registered it, otherwise from your container; `IEnumerable<T>` returns your services, then the cell's.
- **Dispose the container with `DisposeAsync`** (`await using`): `PluginHost` is only `IAsyncDisposable`.
- `AddPlugins` makes the add-on package host-owned automatically.
- Installing a plugin that registers services into a cell whose services were already built gives `PendingRestart`, because a built container can't change.
- If you create the host yourself, call `options.UseDependencyInjection(serviceProvider)` (or `options.UseAutofac(lifetimeScope)`) before `PluginHost.Create`. `AddPlugins` does this for you and also defaults `LoggerFactory` to the container's `ILoggerFactory`.

## 7. WPF

Package `Alcove.Wpf`. WPF finds assemblies by simple name across the whole process and never asks a cell, so plugins' XAML needs help to work inside cells. One call sets it up:

```csharp
using Alcove;
using Alcove.Wpf;
using Alcove.Wpf.Threading;   // OnDispatcher

var options = new PluginHostOptions { Root = root, PluginPackageType = "MyAppPlugin", HostOwnedPackages = { "MyApp.Sdk" } };
options.UseWpf(Application.Current, wpf =>
{
    wpf.ForwardDefaultContextLoads = true;   // opt-in; process-wide while the host lives
    wpf.MapViewModelsToViews = true;         // opt-in: FooViewModel -> FooView
});
await using var host = PluginHost.Create(options);

var catalog = host.Catalog.OnDispatcher(Application.Current.Dispatcher);
catalog.Observe<IToolWindow>(ext => windows.Add(ext.GetInstance()));   // always on the UI thread
await host.LoadAsync();
```

`UseWpf(application)` merges plugins' resources into that application's resources. `UseWpf(dispatcher)` is for a UI thread without a given `Application`: it merges into `WpfPluginOptions.Resources`, or into `Application.Current`'s resources if that isn't set. `UseWpf` makes `Alcove.Wpf` host-owned. It works with `services.AddPlugins` / `UseDependencyInjection` and `UseAutofac` in either order, because the core already creates every extension inside its cell's contextual reflection scope. Calling it twice on the same options throws.

| `WpfPluginOptions` | Default | What it does |
|---|---|---|
| `PreloadWpfAssemblies` | on | Loads each cell's WPF-relevant private assemblies when the cell is created (or grows by a hot install), so a control library used only from XAML is found. |
| `MergePluginResources` | on | Merges each plugin's `Themes/Plugin.xaml`, or the dictionaries it names with `[assembly: PluginResources("...")]`, into `Resources` when it loads, before it registers. Removed when the host is disposed. |
| `ForwardDefaultContextLoads` | off | Forwards WPF's failed default-context loads to the single cell that owns a private copy with that name (see below). |
| `MapViewModelsToViews` | off | Adds implicit `DataTemplate`s for `FooViewModel` → `FooView` (same namespace, or `ViewModels` → `Views`, same assembly). |
| `Resources` | the app's resources | Where plugins' dictionaries and view templates are merged. |

**Set the AppContext switch** in your host's project file, so XAML that refers to its own assembly's dictionaries gets a versioned URI and can't reach another cell's copy of that assembly. If it's missing, `Alcove.Wpf` logs an Information message (event 6009) when the host is created.

```xml
<ItemGroup>
  <RuntimeHostConfigurationOption Include="Switch.System.Windows.Baml2006.AppendLocalAssemblyVersionForSourceUri" Value="true" />
</ItemGroup>
```

**Catalog on the UI thread.** `catalog.OnDispatcher(dispatcher)` returns a `DispatcherExtensionCatalog` whose `Observe` and `ExtensionsAdded` callbacks run on the dispatcher's thread. When they're raised on the UI thread they run inline, so `Observe` called on the UI thread has delivered the existing extensions when it returns. When they're raised on another thread (e.g. after an `InstallAsync` that continued on the thread pool), they're queued with `BeginInvoke`. `GetAll` passes through unchanged.

**Merge precedence.** Each plugin's dictionaries go into one container that is merged **below your app's own merged dictionaries**, so your resources win over any plugin's on a key clash. Plugins merged later win over earlier ones. Define the resources you share with plugins under `SharedResourceKey<T>` keys in your app SDK:

```csharp
using Alcove.Wpf.Resources;

public static class AppKeys
{
    public static readonly SharedResourceKey<Brush> Accent = new("MyApp.Accent");   // namespaced ID
}
Application.Current.Resources.SetResource(AppKeys.Accent, Brushes.SteelBlue);    // plugins use {DynamicResource {x:Static sdk:AppKeys.Accent}}
```

**Threading.** Merging and removing run on the dispatcher's thread: inline when the host loads there, otherwise with a blocking `Dispatcher.Invoke`. Await `LoadAsync`, `InstallAsync` and `DisposeAsync`. Don't block the UI thread on them synchronously (`.Wait()`, `.Result`), or a load on another thread can deadlock with it.

**The forwarder** (`ForwardDefaultContextLoads`) covers WPF's own fallback loads, which bind in the default context: a XAML library that a cell rolled forward to a newer version than another assembly's BAML names, and external theme assemblies. It's registered on `AssemblyLoadContext.Default.Resolving` when the host is created and removed when the host is disposed. While it's registered, it affects **the whole process**, not just your plugins: any failed default-context load whose simple name one of the host's cells owns is answered from that cell. Identical shared copies count as one owner. If several cells own different copies, it doesn't choose; it logs a warning (6003). It's the one opt-in exception to Alcove's "no global state" ([ADR 0012](adr/0012-app-supplies-root-no-global-state.md)), which is why it's off by default.

**List WPF control libraries as host-owned.** Reference third-party control libraries (and your own UserControls and styles, in `MyApp.Sdk`) from the host and list them in `HostOwnedPackages`. Every plugin then uses one copy, and the duplicate-name problem can't arise (architecture §4). Identical private copies in several cells are loaded once and shared ([ADR 0015](adr/0015-share-identical-wpf-assemblies.md)), but different builds with the same `AssemblyVersion` still conflict.

See `samples/WpfHost` ([samples/README.md](../samples/README.md#wpf-host)) for a complete host.

## 8. Trust

```csharp
using Alcove.Trust;

options.TrustPolicy = new TrustPolicy
{
    AllowedFeeds = { "https://nuget.example.com/v3/index.json" },
    RequireSignedPackages = true,
    AllowedSigners = { new AllowedSigner { Kind = SignerKind.Author, Fingerprint = "<sha256 of the signing certificate>" } },
};
```

Package hashes are always pinned. Startup checks file sizes and timestamps cheaply; call `host.VerifyAsync()` for a full re-hash.

**Logging:** `Microsoft.Extensions.Logging.Abstractions` is host-owned, because plugins log through your logger. Your app's version is the highest one plugins can use, so a plugin built against a newer version is reported as incompatible. Keeping the host on a current .NET release avoids this.

Signed packages are always integrity-checked, even without `RequireSignedPackages`. To allow nuget.org packages from specific owners, use `AllowedSigners = [.. AllowedSigner.NuGetOrg("jamesnk")]`. To allow your own self-signed plugins, add `new AllowedSigner { Kind = SignerKind.Author, Fingerprint = "<sha256>", AllowUntrustedRoot = true }`.

**Revocation:** `Revocation = Online` (NuGet's default) checks certificate revocation over the network. On a network that hangs rather than refusing, this can take **about 2 minutes per package**, and cancellation doesn't stop it. For air-gapped or industrial machines, use `Revocation = Offline`. Either way, run installs off the UI thread and show progress. Remember: **plugins run with your app's full permissions.** The trust policy decides *what* may be installed, not what installed code may do.

**Trust issues:** what the trust policy reports appears in `InstallResult.Issues` (and in `ChangeResult` and `RestoreResult`) as `ValidationIssue`s whose `Code` is one of the `TrustIssueCodes` constants: `FeedNotAllowed`, `UnknownSource`, `InvalidPolicy`, `NotSigned`, `Tampered`, `SignerNotAllowed`, `UntrustedCertificate`, `RevocationUnknown`, `NotTimestamped`, `InvalidTimestamp`, `CertificateExpired`, `InvalidSignature`, `VerificationFailed`, `Unreadable` and `IdentityMismatch`. Errors reject the package; warnings (e.g. `RevocationUnknown`) don't.

**Trusted roots:** signature verification needs NuGet's root bundles in `trustedroots/` next to `NuGet.Packaging.dll`. The `Alcove` package copies them to your build and publish output; set `<AlcoveCopyTrustedRoots>false</AlcoveCopyTrustedRoots>` to manage them yourself.

**Feed credentials:** set `PackageFeed.Credentials` (code only), use credentials from the `nuget.config` at `NuGetConfigPath`, or supply an `ICredentialProvider` (`GetCredentialsAsync(feed, isRetry, ct)`, e.g. a login dialog). NuGet's plugin credential providers aren't supported.

## 9. Things to avoid

- **Trimming or single-file publishing** your host. Neither is supported in v1: a single-file host exposes no `deps.json` to build the host inventory from, and `PluginHost.Create` throws `UnsupportedHostException`. Trimming can remove types plugins need. See [open questions](open-questions.md#single-file-and-trimmed-hosts).
- **Shipping WPF control libraries privately in plugins** when you could provide them. List them in `HostOwnedPackages` so every plugin shares one copy. WPF can't cope with duplicate assembly names (see [§7](#7-wpf) and architecture §4).
- **Putting extension points in your app's main assembly.** Put them in a contracts package so plugins can reference them.
- **Breaking contract changes without a major version bump.**

## 10. Extending Alcove

For adapting a DI container other than Microsoft DI or Autofac. Set `PluginHostOptions.Activator` to create extension instances: it's called once per singleton and on every request for a transient, inside the plugin's cell, and gets either the registered factory or the type to create. Set `PluginHostOptions.CellServicesFactory` to give each cell its own service provider: it's asked once per cell, before the cell's plugins register, and only plugins whose entry point implements one of `ServicePluginInterfaces` (matched by full type name, read from metadata) are passed in. The host disposes each provider after the cell's extensions and plugins. `PluginManifest` reads and writes the manifest format, e.g. for tooling.

To hook a UI framework (or anything else) into cells, as `Alcove.Wpf` does, add a factory to `PluginHostOptions.CellObservers` (code only). Each host creates one `ICellObserver` per factory, passing its logger factory, and tells it `CellCreated` (nothing loaded yet), `CellExtended` (a hot install added private assemblies to an existing cell) and `PluginLoading` (a plugin's assemblies are loaded, before it registers; runs in the cell's contextual reflection scope). Calls come on the loading thread, outside the library's locks. An exception from the first two is logged; one from `PluginLoading` faults the plugin. The host disposes observers that implement `IDisposable` / `IAsyncDisposable` when it's disposed.

```csharp
public interface ICellObserver   // Alcove.Cells; every method has an empty default
{
    void CellCreated(CellInfo cell);
    void CellExtended(CellInfo cell, IReadOnlyList<CellAssembly> added);
    void PluginLoading(CellInfo cell, PluginInfo plugin, IReadOnlyList<Assembly> assemblies);
}
// CellInfo:     string Name, AssemblyLoadContext LoadContext, IReadOnlyList<string> Plugins, IReadOnlyList<CellAssembly> PrivateAssemblies
// CellAssembly: string Name, string Path, string PackageId, NuGetVersion PackageVersion, bool IsWpfRelevant
public delegate ICellObserver CellObserverFactory(ILoggerFactory loggerFactory);
```

```csharp
public interface IExtensionActivator   // Alcove.Catalog
{
    object CreateInstance(ExtensionActivationContext context);
}
// ExtensionActivationContext: Type ExtensionPoint, Type? ImplementationType, Func<object>? Factory,
//                             ExtensionLifetime Lifetime, PluginInfo Plugin, IServiceProvider Services

public interface ICellServicesFactory   // Alcove.Cells
{
    IReadOnlyCollection<string> ServicePluginInterfaces { get; }   // e.g. "Alcove.DependencyInjection.IServicePlugin"
    IServiceProvider CreateServices(CellServicesContext context);
}
// CellServicesContext: string Cell, IServiceProvider HostServices, IReadOnlyList<CellServicesPlugin> Plugins
// CellServicesPlugin:  PluginInfo Plugin, IPlugin EntryPoint

public sealed record PluginManifest   // Alcove.Packaging; <PackageId>.plugin.json
{
    public const int CurrentSchemaVersion = 1;
    public int SchemaVersion { get; init; }
    public string? EntryPoint { get; init; }
    public string? Cell { get; init; }
    public bool Isolated { get; init; }
    public IReadOnlyList<string> PrivateDependencies { get; init; }
    public static PluginManifest Parse(string json);
    public static PluginManifest Read(Stream utf8Json);
    public static string GetFileName(string packageId);
    public string ToJson();
}
```

## 11. Providing a harness

Plugin developers want to press F5 and see their plugin running in your app, without packing and installing it every time. Providing that **harness** is your job; Alcove gives you the parts ([ADR 0017](adr/0017-development-plugins.md)). A harness is a program that starts your app (or a stand-in host) with **development plugins**: plugins loaded straight from their build output, next to the installed ones, in their own cells, with the same classification and validation.

**Enabling development plugins is always your explicit choice.** Nothing loads them unless you fill `PluginHostOptions.DevelopmentPlugins` (code only, never read from the configuration file). The convention:

```csharp
using Alcove.Development;

// In the harness's entry point only, never on the app's normal startup path:
var options = MyApp.CreatePluginHostOptions(harnessRoot);       // your usual root, feeds, host-owned packages
options.AddDevelopmentPluginsFrom(args, Environment.GetEnvironmentVariables());
```

`AddDevelopmentPluginsFrom` reads every `--alcove-dev-plugin <folder>` (repeatable; also `--alcove-dev-plugin=<folder>`), the `ALCOVE_DEV_PLUGINS` environment variable (folders separated by `Path.PathSeparator`) and the entry assembly's `[assembly: AlcoveDevelopmentPlugin(...)]` attributes (generated for `PluginUnderTest` items). It leaves the options in `args`. Call it from a harness build, or behind an explicit flag of your own.

What the host does with them:
- **Identity** comes from the `<PackageId>.plugin.json` that `Alcove.Sdk` writes into the build output (`packageId`, `version`); **dependencies** from the plugin's `deps.json`, classified against your host inventory. Cell-private assemblies load from the build output, in place.
- **Validation** as usual, plus: a host-owned package the host can't satisfy is an error (`HostOwnedConflict`, `HostOwnedMissing`); a dependency resolved differently from how an install would resolve it is a warning (`DevelopmentDependencyDiffers`); an unusable folder is an error for that plugin only (`DevelopmentPluginInvalid`).
- An installed plugin with the same ID stays installed but the development plugin loads instead, for this run (`DevelopmentPluginOverridesInstalled`). Plugin dependencies between development and installed plugins work as between installed ones.
- **Nothing is recorded:** no state file entry, no trust policy check, no hash pinning, no cleanup, no staged changes, not in `GetPendingChangesAsync`. `PluginInfo.IsDevelopment` lets your UI show it. Lifecycle calls (`UpdateAsync`, `DisableAsync`, ...) with its ID act on the installed plugin, if any. An install of a plugin with a development plugin's ID while it runs is `PendingRestart`.
- Use a plugin root of your choosing for the harness, typically a separate "harness" root, so authors don't touch their real installation.

**Style 1, a harness project.** Ship a `MyApp.Harness` library package with a `RunAsync(args)` that builds your normal `PluginHostOptions`, calls `AddDevelopmentPluginsFrom` and starts your UI. Make it depend on `Alcove.Sdk` (or tell authors to reference it), so the plugin author's harness project can use `<PluginUnderTest Include="..\MyPlugin\MyPlugin.csproj" />`: the plugin is built first but neither referenced nor copied, and its build output is recorded in a generated attribute.

**Style 2, F5 on the class library (the main one).** Ship your app (or a stand-in host) in the harness package, e.g. under `tools/`, with a `build/MyApp.Harness.props` (and `buildTransitive/`) that sets one property:

```xml
<Project>
  <PropertyGroup>
    <AlcoveHarnessExecutable>$(MSBuildThisFileDirectory)../tools/MyApp.exe</AlcoveHarnessExecutable>
  </PropertyGroup>
</Project>
```

`Alcove.Sdk` then sets `RunCommand`/`RunArguments` (`--alcove-dev-plugin "<build output>"`, plus `AlcoveHarnessArguments`), so `dotnet run` works on the class library, and generates a `Properties/launchSettings.json` "Project" profile with the harness command line if the plugin project has none, which Visual Studio needs to start a class library. The IDE starts your executable itself, so its debugger is attached from the first line. A framework-dependent `.dll` works too (it's started with `dotnet`).

`samples/ConsoleHost.Harness` is a working example of both styles (see [samples/README.md](../samples/README.md)).

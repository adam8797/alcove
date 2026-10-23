# App developer guide

This guide is for people building a **host app** that loads plugins. New to Alcove? Start with the [quick start](quickstart-app-developer.md), which builds a host, its contracts and a harness.

**Contents:** [1. Extension points](#1-define-your-extension-points) · [2. Create and load the host](#2-create-and-load-the-host) · [3. Use extensions](#3-use-extensions) · [4. Manage plugins](#4-manage-plugins) · [5. Cells](#5-cells) · [6. Dependency injection](#6-dependency-injection-optional) · [7. WPF](#7-wpf) · [8. Trust](#8-trust) · [9. Things to avoid](#9-things-to-avoid) · [10. Extending Alcove](#10-extending-alcove) · [11. Providing a harness](#11-providing-a-harness)

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
    public string? PackageId { get; init; }                         // these three: read for development plugins, ignored when installed
    public string? Version { get; init; }
    public IReadOnlyList<string>? PluginDependencies { get; init; }
    public static PluginManifest Parse(string json);
    public static PluginManifest Read(Stream utf8Json);
    public static string GetFileName(string packageId);
    public string ToJson();
}
```

## 11. Providing a harness

Plugin developers want to press F5 and see their plugin running in your app, without packing and installing it every time. Providing that **harness** is your job; Alcove gives you the parts ([ADR 0017](adr/0017-development-plugins.md)). A harness is a program that starts your app (or a stand-in host) with **development plugins**: plugins loaded straight from their build output, next to the installed ones, in their own cells, with the same classification and validation.

The [quick start](quickstart-app-developer.md#4-provide-a-harness) builds one in a few minutes. This section is the detail.

### 11.1 The harness program

**Enabling development plugins is always your explicit choice.** Nothing loads them unless you fill `PluginHostOptions.DevelopmentPlugins` (code only, never read from the configuration file). Do it in the harness's entry point, never on your app's normal startup path:

```csharp
using Alcove.Development;

var options = MyAppHost.CreateOptions(harnessRoot);   // your usual root, feeds, host-owned packages, cells, add-ons
options.AddDevelopmentPluginsFrom(args, Environment.GetEnvironmentVariables());
await MyAppHost.RunAsync(options);                    // your app, as it always starts
```

`AddDevelopmentPluginsFrom` reads every `--alcove-dev-plugin <folder>` (repeatable; also `--alcove-dev-plugin=<folder>`), the `ALCOVE_DEV_PLUGINS` environment variable (folders separated by `Path.PathSeparator`) and the entry assembly's `[assembly: AlcoveDevelopmentPlugin(...)]` attributes (generated for `PluginUnderTest` items). It returns what it added and leaves the options in `args`, so your own argument parsing must ignore them.

Things to decide:
- **Share the startup code.** The harness should start your app the way it always starts: the same options, cells, DI add-on, `UseWpf`, main window. Put that in a method both entry points call (`MyAppHost.CreateOptions` and `RunAsync` above), in your app's project or a library both reference.
- **The plugin root.** Use a root of the harness's own (e.g. `%LOCALAPPDATA%\MyApp\harness-plugins`), so plugin developers don't touch their real installation. Plugins installed there load next to the development plugins, which is how a plugin developer tests against the plugins theirs depends on. Consider accepting a `--root` argument; plugin developers can pass it with `AlcoveHarnessArguments`.
- **Feeds.** Keep your normal feeds, so plugin developers can install other plugins into the harness root from the running harness.
- **Show what's happening.** Print or show `ValidateAsync`'s issues and mark development plugins in your plugin list (`PluginInfo.IsDevelopment`). Plugin developers rely on the validation codes ([plugin guide](plugin-developer-guide.md#troubleshooting-a-development-plugin)). `FailFast = true` makes the first plugin fault throw, which some prefer while debugging.
- **Version it with your app.** The harness is your app, so release `MyApp.Harness 2.3.0` with `MyApp 2.3.0` and `MyApp.Contracts 2.3.0`. Plugin developers reference a harness version in the range of contracts they build against.

### 11.2 Packaging the harness (F5 on the plugin project)

The main style: plugin developers reference your harness package and press F5 on their class library. The package holds the harness executable and everything it needs under `tools/`, and a `build/` props file that sets one `Alcove.Sdk` property:

```xml
<!-- MyApp.Harness/MyApp.Harness.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>                 <!-- or WinExe, with net10.0-windows, for a WPF app -->
    <TargetFramework>net10.0</TargetFramework>
    <PackageId>MyApp.Harness</PackageId>
    <Description>Runs MyApp with your plugin loaded from its build output.</Description>
    <IncludeBuildOutput>false</IncludeBuildOutput>                 <!-- nothing under lib/ -->
    <SuppressDependenciesWhenPacking>true</SuppressDependenciesWhenPacking>   <!-- the harness brings its own -->
    <DevelopmentDependency>true</DevelopmentDependency>
    <TargetsForTfmSpecificContentInPackage>$(TargetsForTfmSpecificContentInPackage);AddHarnessToPackage</TargetsForTfmSpecificContentInPackage>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../MyApp/MyApp.csproj" />
    <None Include="build/MyApp.Harness.props" Pack="true" PackagePath="build/;buildTransitive/" />
  </ItemGroup>
  <!-- The whole build output under tools/, including trustedroots/ (signature verification needs it). -->
  <Target Name="AddHarnessToPackage">
    <ItemGroup>
      <TfmSpecificPackageFile Include="$(OutDir)**/*" PackagePath="tools/%(RecursiveDir)%(Filename)%(Extension)" />
    </ItemGroup>
  </Target>
</Project>
```

```xml
<!-- MyApp.Harness/build/MyApp.Harness.props -->
<Project>
  <PropertyGroup>
    <AlcoveHarnessExecutable>$(MSBuildThisFileDirectory)../tools/MyApp.Harness.dll</AlcoveHarnessExecutable>
  </PropertyGroup>
</Project>
```

`AlcoveHarnessExecutable` may name the `.exe` or the framework-dependent `.dll` (started with `dotnet`, so it also works on Linux and macOS). With it, `Alcove.Sdk` in the plugin project:
- sets `RunCommand`/`RunArguments` (`--alcove-dev-plugin "<build output>"`, plus the plugin's `AlcoveHarnessArguments`), so `dotnet run` and `dotnet watch` work on the class library;
- generates `Properties/launchSettings.json` if the plugin project has none, which Visual Studio and Rider need to start a class library. It has two profiles with the harness command line, because the IDEs need different ones: "*project* in harness" (`Project`, Visual Studio's default; it takes the program from `RunCommand`) and "*project* in harness (Rider)" (`Executable`; Rider runs the class library itself for a `Project` profile). Both were checked on Windows with breakpoints and Hot Reload ([results](design/development-plugins.md#windows-results-visual-studio-2026-2026-10-21)).

The IDE starts your executable itself, so its debugger is attached from the first line.

**Tell plugin developers to reference it with `PrivateAssets="all"`:**

```xml
<PackageReference Include="MyApp.Harness" Version="2.3.*" PrivateAssets="all" />
```

`DevelopmentDependency` only makes NuGet add `PrivateAssets="all"` when an IDE or `dotnet add package` adds the reference. Without it, the harness becomes a dependency of the plugin's package, and every install of the plugin downloads your harness.

The harness package doesn't need `Alcove.Sdk`; your app's dependencies come with it in `tools/`. If your contracts package's `build/` props are where plugin developers get `PluginPackageType` from, they still reference the contracts package too.

### 11.3 A harness library (a harness project per plugin)

The alternative: ship a library package, e.g. `MyApp.Harness` with a `HarnessApp.RunAsync(args)` that builds your options, calls `AddDevelopmentPluginsFrom` and starts your app. A plugin developer adds a small exe project that references it and names their plugin with `<PluginUnderTest Include="..\Contoso.Charts\Contoso.Charts.csproj" />` ([plugin guide §7](plugin-developer-guide.md#a-harness-project)). The plugin is built first but neither referenced nor copied, and its build output is recorded in a generated attribute. Make the library depend on `Alcove.Sdk`, or tell plugin developers to reference it, so `PluginUnderTest` is available.

It costs plugin developers a project, but it's an ordinary exe, so every IDE runs it without launch profiles. It's also the way to test several plugins together. You can offer both: the same harness code as a library and, packed as in 11.2, as a tool.

### 11.4 What development plugins get

- **Identity** comes from the `<PackageId>.plugin.json` that `Alcove.Sdk` writes into the build output (`packageId`, `version`), **dependencies** from the plugin's `deps.json`, classified against your host inventory as an install would classify them. Cell-private assemblies load from the build output, in place. Plugin dependencies are the ones the manifest's `pluginDependencies` names (project and package references to plugins), or whose ID is an installed or development plugin.
- **Validation** as usual, plus: a host-owned package the host can't satisfy is an error (`HostOwnedConflict`, `HostOwnedMissing`); a dependency resolved differently from how an install would resolve it is a warning (`DevelopmentDependencyDiffers`); a plugin dependency that's neither installed nor a development plugin is an error (`PluginDependencyMissing`); an unusable folder is an error for that plugin only (`DevelopmentPluginInvalid`, whose `PluginId` is the project's name, or the folder, when the plugin can't be identified).
- An installed plugin with the same ID stays installed, but the development plugin loads instead, for this run (`DevelopmentPluginOverridesInstalled`). Plugin dependencies between development and installed plugins work as between installed ones.
- **Nothing is recorded:** no state file entry, no trust policy check, no hash pinning, no cleanup, no staged changes, nothing in `GetPendingChangesAsync`. `PluginInfo.IsDevelopment` lets your UI show it. Lifecycle calls (`UpdateAsync`, `DisableAsync`, ...) with its ID act on the installed plugin, if any. An install of a plugin with a development plugin's ID while it runs is `PendingRestart`. Installs don't resolve against development plugins: installing a plugin that depends on one installs that dependency from your feeds too.
- **No shadow copies.** The plugin loads in place, so on Windows its DLLs are locked while the harness runs, as for any app being debugged. Hot Reload works (11.2); otherwise plugin developers stop, rebuild and start again.

### 11.5 Testing your harness

Before you publish it, try it as a plugin developer would:

1. Pack your contracts and the harness into a local folder feed.
2. Create a plugin project that references `Alcove.Sdk`, your contracts and your harness (`PrivateAssets="all"`), as in the [plugin quick start](quickstart-plugin-developer.md).
3. `dotnet run` on it: your app starts with `Development plugin: ...` and the plugin's extensions. Then F5 in Visual Studio and in Rider, with a breakpoint.
4. `dotnet pack` the plugin and check its `.nuspec`: your contracts are a dependency, your harness isn't.

`samples/ConsoleHost.Harness` is a working harness (both styles, unpackaged; see [samples/README.md](../samples/README.md)). The harness package layout above was checked end to end with it.

# Plugin developer guide

This guide is for people writing a **plugin** for a host app.

## 1. Create the project

A plugin is a class library targeting a framework the host supports (for a WPF host, e.g. `net10.0-windows` with `<UseWPF>true</UseWPF>`).

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <PackageId>Contoso.Charts</PackageId>
    <Version>1.0.0</Version>
    <Title>Charts</Title>
    <Description>Chart tool windows for MyApp.</Description>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Alcove.Sdk" Version="1.*" PrivateAssets="all" />
    <PackageReference Include="MyApp.Contracts" Version="[2.0,3.0)" />
    <PackageReference Include="SomeChartLibrary" Version="4.2.0" />
  </ItemGroup>
</Project>
```

`dotnet pack` produces a plugin package. `Alcove.Sdk`:
- sets the NuGet `PackageType` from `<PluginPackageType>`, which the host's contracts package usually sets for you;
- generates the plugin manifest;
- keeps host-owned packages (`Alcove.Abstractions`, `NuGet.Versioning`, `Microsoft.Extensions.Logging.Abstractions` and whatever the host's contracts package lists as `PluginHostOwnedPackage`) as NuGet dependencies, but never copies their assemblies into your build output;
- sets an explicit `AssemblyVersion` (major.minor.patch.0 from `Version`) unless you set one.

Title, description, icon, authors and so on come from ordinary NuGet properties.

`Alcove.Abstractions` normally comes in through the host's contracts package, which depends on it (its attributes derive from `ExtensionAttribute`). If the host's contracts don't, add `<PackageReference Include="Alcove.Abstractions" Version="..." />` yourself. The SDK can't add it for you, because restore doesn't see references that a package's own build files add.

The SDK reports its errors with codes `ALCOVE001`–`ALCOVE006`, e.g. `ALCOVE001` when `PluginPackageType` is missing.

## 2. Contribute extensions

**With an entry point.** If your plugin has exactly one public, non-abstract class implementing `IPlugin` (it needs a public parameterless constructor), Alcove finds it. With several, set `<PluginEntryPoint>Contoso.Charts.ChartsPlugin</PluginEntryPoint>` (validation error `EntryPointAmbiguous` otherwise). A plugin with an entry point registers its extensions itself; `[Extension]` attributes are only scanned in plugins without one.

```csharp
public sealed class ChartsPlugin : IPlugin
{
    public void Register(IExtensionRegistrar registrar, IPluginContext context)
    {
        registrar.Add<IToolWindow>(() => new ChartWindow(context.DataDirectory),
            new ToolWindowAttribute { Title = "Charts" });
    }

    public async ValueTask InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        await ChartCache.WarmUpAsync(context.DataDirectory, ct);   // optional
    }
}
```

Each registration chooses a lifetime: `Singleton` (default, one shared instance) or `Transient` (a new instance each time, e.g. a window that can be open twice):

```csharp
registrar.Add<IToolWindow>(() => new ChartWindow(context.DataDirectory),
    new ToolWindowAttribute { Title = "Charts" }, ExtensionLifetime.Transient);
```

**Without an entry point (attributes only):**

```csharp
[ToolWindow(Title = "Charts", Lifetime = ExtensionLifetime.Transient)]
public sealed class ChartWindow : IToolWindow { ... }
```

Only attributed types are discovered. Implementing an interface alone isn't enough. Without a dependency injection add-on, an attribute-discovered or `Add<T, TImplementation>` extension needs a public parameterless constructor.

The **plugin context** gives you your ID and version, a private `DataDirectory`, a `Logger`, and `Services` (whatever the host offers).

## 3. Dependencies

| Your dependency is… | What happens |
|---|---|
| The host's contracts / SDK (host-owned) | Host's copy is used. If you need a newer version than the host has, your plugin is **incompatible** with that host. |
| `Alcove.Abstractions`, `NuGet.Versioning`, `Microsoft.Extensions.Logging.Abstractions` (and its dependency `Microsoft.Extensions.DependencyInjection.Abstractions`) (host-owned) | Always the host's copy. Referencing a newer `Microsoft.Extensions.Logging.Abstractions` than the host ships, directly or through another package such as `Microsoft.Extensions.Http`, makes your plugin incompatible with that host. |
| Something the host also ships, compatible version | Host's copy is used. |
| Something the host also ships, incompatible version | You get a private copy in your cell. |
| Something the host doesn't ship | Private copy in your cell. |
| Another plugin | It's installed and loaded too, before yours. |

To always use your own copy of a dependency:

```xml
<PluginPrivateDependency Include="Newtonsoft.Json" />
```

Privacy is per **cell**, not per plugin: other plugins sharing your cell share your private copies.

**If other plugins depend on yours,** avoid exposing third-party types (e.g. `JObject`, a charting library's types) in your public API. Types that do appear there become **exposed dependencies**. Every plugin using yours is forced onto your cell's copy, and an incompatible version requirement on their side becomes a validation error (`ExposedDependencyConflict`). Expose your own types or BCL types instead.

### WPF in plugins

WPF finds XAML resources and types by assembly **name** across the whole process, regardless of cells. Most recently loaded wins. *(spike: wpf-across-load-contexts, pending Windows confirmation)*

- `Alcove.Sdk` sets an explicit `AssemblyVersion`, so your own `InitializeComponent` URIs include the version. Keep it that way.
- Reference other assemblies' resource dictionaries with a versioned URI (`/Lib;v2.0.0.0;component/...`), not `/Lib;component/...`. URI helpers are planned for `Alcove.Wpf` (on hold).
- Prefer control libraries the **host** provides (host-owned) over bundling your own copy.
- A control library used only from XAML (never touched from code) must be loaded before the XAML runs. Until the planned `Alcove.Wpf` add-on pre-loads them, touch a type from code first (e.g. `_ = typeof(SomeControl);`).
- If another plugin bundles a different build of the same control library with the **same `AssemblyVersion`**, the two will conflict. The library reports this, but can't fix it.

## 4. Cells

```xml
<PropertyGroup>
  <!-- Share a load context with other plugins that name the same cell -->
  <PluginCell>Contoso.Shared</PluginCell>

  <!-- OR: require a cell of your own; validation fails if anything else is put in it -->
  <PluginIsolated>true</PluginIsolated>
</PropertyGroup>
```

## 5. Dependency injection (if the host uses it)

Reference `Alcove.DependencyInjection` and implement:

```csharp
using Alcove;
using Alcove.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

public sealed class ChartsPlugin : IPlugin, IServicePlugin
{
    public void ConfigureServices(IServiceCollection services)
        => services.AddSingleton<IChartTheme, DefaultChartTheme>();

    public void Register(IExtensionRegistrar registrar, IPluginContext context)
        => registrar.Add<IToolWindow, ChartWindow>(new ToolWindowAttribute { Title = "Charts" });
}
```

`ChartWindow` gets constructor injection from your cell's services plus the host's. Use `Alcove.Autofac` (`IServicePlugin.ConfigureContainer(ContainerBuilder)`) if the host uses Autofac.

## 6. Lifecycle you should expect

- A fresh install loads **immediately** when possible, so don't assume the app just started.
- Updates and uninstalls apply at the **next startup**. Your code is never unloaded mid-run.
- If you throw during `Register`, `InitializeAsync`, `ConfigureServices`, or while an extension is created, your plugin is marked **Faulted** and the host is told. Plugins that depend on yours aren't loaded; other plugins keep loading.
- You run with the host's full permissions. Behave accordingly.
- When the host is disposed, your singleton extensions and then your entry point are disposed (`IAsyncDisposable` or `IDisposable`), in reverse load order. An `InitializeAsync` still running gets a cancelled token. During the host's call the token follows the app's token; after `InitializeAsync` returns it's cancelled only when the host shuts down, so you can tie background work to it. The host waits for `InitializeAsync` to finish, so honour the token.
- `DataDirectory` (`<root>/data/<id>`) is created on first access and survives updates and uninstalls. `Logger` uses your plugin ID as its category.

## 7. Publish

Push the `.nupkg` to whatever feed the host uses. Signing is optional unless the host's trust policy requires it.

If you sign, **always timestamp**:

```
dotnet nuget sign MyPlugin.1.0.0.nupkg --certificate-path cert.pfx --timestamper http://timestamp.digicert.com
```

Without a timestamp, the signature becomes invalid when your certificate expires. A self-signed certificate is fine for private distribution: the host allow-lists its SHA-256 fingerprint with `AllowUntrustedRoot`. On Linux, `dotnet nuget sign` works with self-signed certificates, but not with certificates from a private CA.

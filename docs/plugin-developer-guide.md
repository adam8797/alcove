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
- sets an explicit `AssemblyVersion` (major.minor.patch.0 from `Version`) unless you set one;
- on every build, writes the manifest (with your `PackageId` and version) into the build output and sets `EnableDynamicLoading=true` (unless you set it), so dependencies are copied next to your plugin: the build output loads as a development plugin ([§7](#7-testing-your-plugin)).

Title, description, icon, authors and so on come from ordinary NuGet properties.

`Alcove.Abstractions` normally comes in through the host's contracts package, which depends on it (its attributes derive from `ExtensionAttribute`). If the host's contracts don't, add `<PackageReference Include="Alcove.Abstractions" Version="..." />` yourself. The SDK can't add it for you, because restore doesn't see references that a package's own build files add.

The SDK reports its errors with codes `ALCOVE001`–`ALCOVE006`, e.g. `ALCOVE001` when `PluginPackageType` is missing.

## 2. Contribute extensions

**With an entry point.** If your plugin has exactly one public, non-abstract class implementing `IPlugin` (it needs a public parameterless constructor), Alcove finds it. With several, set `<PluginEntryPoint>Contoso.Charts.ChartsPlugin</PluginEntryPoint>` (validation error `EntryPointAmbiguous` otherwise). A plugin with an entry point registers its extensions itself; `[Extension]` attributes are only scanned in plugins without one.

```csharp
using Alcove;   // everything a plugin needs is in this namespace

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

WPF finds XAML resources and types by assembly **name** across the whole process, regardless of cells. Most recently loaded wins. *(spike: wpf-across-load-contexts, confirmed on Windows)* A host that uses `Alcove.Wpf` (`options.UseWpf(...)`) handles most of this for you. Reference `Alcove.Wpf` yourself only if you use its types (`PluginResources`, `PluginPackUri`, `SharedResourceKey<T>`, all in `Alcove.Wpf.Resources`). With `Alcove.Sdk` it's treated as host-owned automatically, so it stays a dependency but is never copied into your output.

**Your resource dictionary.** Put your styles, brushes and templates in `Themes/Plugin.xaml` (a `ResourceDictionary`, built as a `Page`). The host merges it into the app's resources when your plugin loads, before `Register` runs, so your XAML can use it as a `StaticResource`. To merge other or several dictionaries instead, name them in the order you want:

```csharp
using Alcove.Wpf.Resources;

[assembly: PluginResources("Themes/Colors.xaml")]
[assembly: PluginResources("Themes/Templates.xaml")]
```

A declared dictionary that doesn't exist faults your plugin. Every plugin shares the app's resources, so **prefix your keys with your plugin ID** (`x:Key="Contoso.Charts.AxisBrush"`). The app's own resources win over yours on a clash, and a plugin merged later wins over earlier ones.

**The app's resources.** Use the keys from the host's SDK, not string literals. They're `SharedResourceKey<T>` constants:

```xml
<Border Background="{DynamicResource {x:Static sdk:AppKeys.Accent}}" />
```

From code, use `element.SetResourceReference(Border.BackgroundProperty, AppKeys.Accent)` or `element.TryFindResource(AppKeys.Accent, out var brush)`. Use `DynamicResource` for anything the app may change at run time, such as a theme.

**Pack URIs.** When you refer to XAML from code (`ResourceDictionary.Source`, `Application.LoadComponent`), use `PluginPackUri` instead of writing the URI yourself. It always includes the loaded assembly's version (and public key token), so it can't reach another cell's copy:

```csharp
using Alcove.Wpf.Resources;

var colors = new ResourceDictionary { Source = PluginPackUri.Create(typeof(ChartView).Assembly, "Themes/Colors.xaml") };
// /Contoso.Charts;v1.0.0.0;component/Themes/Colors.xaml
```

In XAML, reference other assemblies' dictionaries with a versioned URI (`/Lib;v2.0.0.0;component/...`), not `/Lib;component/...`.

**Views for view-models.** If the host turns on `MapViewModelsToViews`, each public `FooViewModel` in your assembly is shown by `FooView` wherever a `ContentControl` (or an `ItemsControl` item) displays it. The view must be in the same namespace, or in the namespace with `ViewModels` replaced by `Views` (`Contoso.Charts.ViewModels.ChartViewModel` → `Contoso.Charts.Views.ChartView`). It must be **in the same assembly**, and be a `FrameworkElement` with a public parameterless constructor. You can also build the templates yourself with `ViewTemplates.Create(typeof(ChartViewModel), typeof(ChartView))` (`Alcove.Wpf.Views`).

**Cautions:**
- `Alcove.Sdk` sets an explicit `AssemblyVersion`, so your own `InitializeComponent` URIs include the version. Keep it that way.
- Prefer control libraries the **host** provides (host-owned) over bundling your own copy.
- A control library used only from XAML (never touched from code) must be loaded before the XAML runs. `Alcove.Wpf` pre-loads such libraries into your cell (on by default). In a host without it, touch a type from code first (e.g. `_ = typeof(SomeControl);`).
- Two plugins bundling the **same package and version** of a control library share one copy across their cells ([ADR 0015](adr/0015-share-identical-wpf-assemblies.md)), so its static state is shared too.
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

## 7. Testing your plugin

You don't need to pack, publish and install to try your plugin: if the host provides a **harness**, the plugin runs straight from its build output (`bin/Debug/net10.0/`) as a **development plugin**, in its own cell with the same isolation and validation as when installed, next to the host's installed plugins. Press F5 and the app starts with your plugin loaded and the debugger attached; breakpoints in your plugin just hit. To change code, stop debugging (which closes the app), rebuild and press F5 again, as for any app. On Windows the running app locks your DLLs, as it would for any app being debugged.

There are two ways, depending on what the host provides.

**F5 on the plugin project (the usual one).** The host ships a harness package, say `MyApp.Harness`. Reference it from the plugin project:

```xml
<PackageReference Include="MyApp.Harness" Version="2.*" PrivateAssets="all" />
```

It sets `AlcoveHarnessExecutable`, so:
- `dotnet run` on your class library starts the harness with `--alcove-dev-plugin "<your build output>"`;
- a `Properties/launchSettings.json` with a **Project** profile (the harness command line) is generated on the first build, if your project has none, so Visual Studio and Rider can start (and debug, with Hot Reload) the class library. It holds machine-specific paths: delete it to regenerate it (e.g. after changing the harness), or keep your own launch profile instead (`AlcoveGenerateLaunchSettings=false` turns generation off).

Without a harness package, set the property yourself: `<AlcoveHarnessExecutable>path/to/MyApp.Harness.exe</AlcoveHarnessExecutable>` (a framework-dependent `.dll` works too: it's started with `dotnet`). `AlcoveHarnessArguments` adds arguments after the development plugin.

**A harness project.** Add a small exe project that starts the harness and names your plugin as a `PluginUnderTest`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="MyApp.Harness" Version="2.*" />
    <PackageReference Include="Alcove.Sdk" Version="1.*" PrivateAssets="all" />
    <PluginUnderTest Include="..\Contoso.Charts\Contoso.Charts.csproj" />
  </ItemGroup>
</Project>
```

```csharp
await MyApp.Harness.HarnessApp.RunAsync(args);   // whatever the host's harness package offers
```

`PluginUnderTest` builds your plugin first but never references it or copies it into the harness output (it must not load into the app's default context), and records its build output folder in the harness assembly, so no command-line arguments are needed. Start the harness project with F5.

**What's different from an installed plugin.** Your plugin's dependencies come from its `deps.json`, classified as an install would classify them against the host. Where the build differs from what an install would load, validation warns (`DevelopmentDependencyDiffers`, e.g. the host has a newer version of a host-shared package, so that's the one you run with); a host-owned package the host can't satisfy is an error, just as after a real install (`HostOwnedConflict`, `HostOwnedMissing`). An installed plugin with the same ID is left installed but isn't loaded while yours runs (`DevelopmentPluginOverridesInstalled`). Your plugin may depend on installed plugins or other development plugins. A development plugin is never recorded in the host's state file, trust-verified or cleaned up; `PluginInfo.IsDevelopment` tells the host it's one.

**Hot Reload.** Not guaranteed, but `dotnet watch` on the plugin project (or on the harness project) applies method-body edits to the running plugin; see the [design notes](design/development-plugins.md#spike-results).

## 8. Publish

Push the `.nupkg` to whatever feed the host uses. Signing is optional unless the host's trust policy requires it.

If you sign, **always timestamp**:

```
dotnet nuget sign MyPlugin.1.0.0.nupkg --certificate-path cert.pfx --timestamper http://timestamp.digicert.com
```

Without a timestamp, the signature becomes invalid when your certificate expires. A self-signed certificate is fine for private distribution: the host allow-lists its SHA-256 fingerprint with `AllowUntrustedRoot`. On Linux, `dotnet nuget sign` works with self-signed certificates, but not with certificates from a private CA.

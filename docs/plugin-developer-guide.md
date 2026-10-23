# Plugin developer guide

This guide is for people writing a **plugin** for a host app. New to Alcove? Start with the [quick start](quickstart-plugin-developer.md).

**Contents:** [0. Set up](#0-set-up) · [1. Create the project](#1-create-the-project) · [2. Contribute extensions](#2-contribute-extensions) · [3. Dependencies](#3-dependencies) · [4. Cells](#4-cells) · [5. Dependency injection](#5-dependency-injection-if-the-host-uses-it) · [6. Lifecycle](#6-lifecycle-you-should-expect) · [7. Run and debug your plugin](#7-run-and-debug-your-plugin) · [8. Publish](#8-publish)

## 0. Set up

- **The .NET SDK** for the framework the host targets (e.g. .NET 10).
- **An IDE**, if you want one. Visual Studio 2026 and Rider are checked for F5, breakpoints and Hot Reload on a development plugin. VS Code with C# Dev Kit isn't yet, but `dotnet run` and `dotnet watch` work anywhere.
- **The host's packages:** its contracts package (the extension points; it also configures `Alcove.Sdk` for that host), maybe an app SDK with shared UI and helpers, and its harness package (§7). Ask the host's developers which feed they publish to, and add it to a `nuget.config` next to your solution if it isn't nuget.org.
- **`Alcove.Sdk`**, from nuget.org. A harness needs a version after `0.0.1-alpha`.
- **Your `.gitignore`:** add `Properties/launchSettings.json`. It's generated, with machine-specific paths (§7).

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
- on every build, writes the manifest (with your `PackageId` and version) into the build output and sets `EnableDynamicLoading=true` (unless you set it), so dependencies are copied next to your plugin: the build output loads as a development plugin ([§7](#7-run-and-debug-your-plugin)).

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

## 7. Run and debug your plugin

You don't need to pack, publish and install your plugin to try it. If the host provides a **harness**, your plugin runs straight from its build output (`bin/Debug/net10.0/`) as a **development plugin**: in its own cell, with the same isolation and validation as when installed, next to the plugins installed in the harness's plugin root. Press F5 and the app starts with your plugin loaded and the debugger attached from the first line.

There are two ways to run it, depending on what the host ships: F5 on your plugin project (the usual one), or a small harness project of your own.

### F5 on the plugin project

The host ships a harness package, say `MyApp.Harness`. Reference it from your plugin project, **with `PrivateAssets="all"`**:

```xml
<PackageReference Include="MyApp.Harness" Version="2.*" PrivateAssets="all" />
```

`PrivateAssets="all"` keeps the harness out of your package's dependencies. Without it, every install of your plugin would also download the harness. (The harness package is marked as a development dependency, but NuGet only applies that when an IDE or `dotnet add package` adds the reference.)

The harness package sets `AlcoveHarnessExecutable`, so:
- `dotnet run` on your class library starts the harness with `--alcove-dev-plugin "<your build output>"`;
- the first build writes `Properties/launchSettings.json`, if your project has none, with two launch profiles holding the harness command line, because Visual Studio and Rider need different ones (below; [why](design/development-plugins.md#launch-profiles-and-hot-reload-why-visual-studio-and-rider-need-different-ones)). It holds machine-specific paths, so don't commit it. Delete it to regenerate it, e.g. after changing `AlcoveHarnessArguments` or updating the harness package. To keep your own launch profiles instead, set `<AlcoveGenerateLaunchSettings>false</AlcoveGenerateLaunchSettings>`.

To pass more arguments to the harness (after `--alcove-dev-plugin`), set `AlcoveHarnessArguments`, e.g. a plugin root: `<AlcoveHarnessArguments>--root "$(MSBuildThisFileDirectory)harness-root"</AlcoveHarnessArguments>`. Which arguments the harness accepts is up to the host. Without a harness package, point at the harness yourself: `<AlcoveHarnessExecutable>path/to/MyApp.Harness.exe</AlcoveHarnessExecutable>` (a framework-dependent `.dll` works too: it's started with `dotnet`).

**Visual Studio 2026.** Use the profile **"*project* in harness"**, the default. F5 starts the harness under the debugger, breakpoints in your plugin hit, and Hot Reload (Alt+F10, or the 🔥 button) applies to your plugin in its cell. It's a `Project` profile: Visual Studio takes the program from the project (the harness that `Alcove.Sdk` set) and the arguments from the profile. If your `launchSettings.json` came from an older `Alcove.Sdk` (a single `Executable` profile), Hot Reload fails with "Hot Reload is not supported in this session due to the lack of a managed language service": delete the file to regenerate it.

**Rider.** Use the profile **"*project* in harness (Rider)"** (an `Executable` profile). Rider lists the other one too, but runs your class library itself for it, which fails with `MissingMethodException: Entry point not found`. Debugging, breakpoints and Hot Reload ("Apply changes") work with the Rider profile. Rider also keeps run configurations for profiles that no longer exist; ignore those.

**VS Code and the command line.** `dotnet run` runs the harness; `dotnet watch` runs it with Hot Reload. F5 in C# Dev Kit hasn't been checked with a class library. If it doesn't offer the launch profile, attach to the harness process, or add a `coreclr` configuration to `launch.json` with the program and arguments from `launchSettings.json`.

### A harness project

Some hosts ship their harness as a library, with something like `MyApp.Harness.HarnessApp.RunAsync(args)`. Then add a small exe project that names your plugin as a `PluginUnderTest`, and start that one:

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

`PluginUnderTest` builds your plugin first, but never references it or copies it into the harness output (it must not load into the app's default context). It records your plugin's build output folder in the harness assembly, so no command-line arguments are needed. The harness project is an ordinary exe, so F5, breakpoints and Hot Reload work in every IDE, and building it rebuilds your plugin. Several `PluginUnderTest` items load several development plugins.

### Editing while it runs

- **Hot Reload** applies method-body edits (and many others) to the running app: in Visual Studio and Rider with the profiles above, or with `dotnet watch`.
- **Rude edits**, changes that can't be applied to loaded code (e.g. a class's modifiers or base type), get an offer to rebuild and restart. Accept it, and the app comes back with your change.
- **Otherwise: stop, edit, F5.** The IDE rebuilds your plugin first. Your code is never unloaded mid-run, and on Windows the running app keeps your DLL locked, so a build while debugging fails with "The file is locked by: .NET Host" (MSB3026, MSB3027). Stop debugging first.

### What's different from an installed plugin

- Your dependencies come from your `deps.json`, classified as an install would classify them against the host. Where the build differs from what an install would load, validation warns (`DevelopmentDependencyDiffers`): for example, the host has a newer version of a host-shared package, so you run with the host's. A host-owned package the host can't satisfy is an error, as it would be after a real install (`HostOwnedConflict`, `HostOwnedMissing`).
- An installed plugin with your ID stays installed, but isn't loaded while yours runs (`DevelopmentPluginOverridesInstalled`).
- Your plugin may depend on plugins installed in the harness's root, or on other development plugins. A plugin dependency that's neither is an error (`PluginDependencyMissing`), as for an install. `Alcove.Sdk` records your plugin dependencies (project and package references to plugins) in the manifest in your build output, so the harness can tell them from ordinary libraries.
- A development plugin is never recorded in the host's state file, trust-verified, hash-pinned or cleaned up. `PluginInfo.IsDevelopment` tells the host it's one. Installing a plugin with your ID while yours runs is `PendingRestart`.

### Troubleshooting a development plugin

The harness's validation report names your plugin and a code:

| You see | Why | What to do |
|---|---|---|
| `DevelopmentPluginInvalid`: the folder doesn't exist | Not built yet, or the path is wrong | Build the plugin. Check that `--alcove-dev-plugin` points at `bin/<configuration>/<framework>/` |
| `DevelopmentPluginInvalid`: no plugin manifest | Built without `Alcove.Sdk`, or with one that predates development plugins | Reference a current `Alcove.Sdk` and rebuild |
| `DevelopmentPluginInvalid`: a file its deps.json lists is missing | A partial build | Rebuild |
| `DevelopmentPluginOverridesInstalled` (warning) | Your plugin is also installed in the harness's root | Nothing: yours runs. Uninstall it from the harness root to silence the warning |
| `DevelopmentDependencyDiffers` (warning) | The host ships a newer version of one of your dependencies | Usually nothing: installed, you'd get the host's copy too. Build against that version to match |
| `HostOwnedConflict`, `HostOwnedMissing` | You reference a newer version of a host-owned package (the host's contracts, `Microsoft.Extensions.Logging.Abstractions`, ...) than the host has, or one it doesn't have | Build against the version the host ships |
| `PluginDependencyMissing` | Your plugin depends on another plugin the harness can't find | Install it into the harness's root, or run it as a development plugin too (another `--alcove-dev-plugin`, or another `PluginUnderTest`) |
| Rider: `Entry point not found in assembly '<your plugin>'` | Rider ran your class library itself (the Visual Studio profile) | Pick the "(Rider)" profile |
| Visual Studio: "lack of a managed language service" on Hot Reload | An `Executable` launch profile from an older `Alcove.Sdk` | Delete `Properties/launchSettings.json` and rebuild |
| The harness starts with old arguments | `launchSettings.json` is only generated when it's missing | Delete it and rebuild |
| `ALCOVE001` when building | No `PluginPackageType`; the host's contracts package usually sets it | Reference the host's contracts package |

### Try a real install before publishing

`dotnet pack -c Release -o <folder>`, then install your plugin from that folder in the host or the harness, if its options list the folder as a feed. That's the path your users take: resolution, download, trust policy, hash pinning, and a restart for updates.

## 8. Publish

Push the `.nupkg` to whatever feed the host uses. Signing is optional unless the host's trust policy requires it.

If you sign, **always timestamp**:

```
dotnet nuget sign MyPlugin.1.0.0.nupkg --certificate-path cert.pfx --timestamper http://timestamp.digicert.com
```

Without a timestamp, the signature becomes invalid when your certificate expires. A self-signed certificate is fine for private distribution: the host allow-lists its SHA-256 fingerprint with `AllowUntrustedRoot`. On Linux, `dotnet nuget sign` works with self-signed certificates, but not with certificates from a private CA.

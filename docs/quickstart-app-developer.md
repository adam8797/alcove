# Quick start: app developers

From nothing to a host app that loads plugins, plus a harness that lets plugin developers press F5. About half an hour. The [app developer guide](app-developer-guide.md) has the details behind each step.

You'll build three things:

| Project | What it is | Who uses it |
|---|---|---|
| `MyApp.Contracts` | A class library package with your extension points | You and every plugin |
| `MyApp` | Your app: it creates a `PluginHost` and uses the extensions | Your users |
| `MyApp.Harness` | Your app started with development plugins, packed as a package | Plugin developers, from their IDE |

The examples are a console app. A WPF app works the same way, plus [`Alcove.Wpf`](app-developer-guide.md#7-wpf).

## 0. Prerequisites

- The .NET 10 SDK.
- A NuGet feed for your contracts and plugins. A local folder works to begin with (`mkdir C:\feeds\myapp`); later, your company feed or nuget.org. Add it to a `nuget.config` next to your projects.
- **Alcove packages with development plugin support**, which step 4 needs: a version after `0.0.1-alpha`. Until one is on nuget.org, build them from the Alcove repository (`dotnet build -c Release` puts them in `artifacts/packages/Release/`, as version `0.1.0-dev`) and add that folder as a feed too.
- The Alcove version you use, in one place. A `Directory.Build.props` next to your projects:

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AlcoveVersion>0.1.0-dev</AlcoveVersion>   <!-- or the newest release -->
  </PropertyGroup>
</Project>
```

## 1. Define your extension points

`MyApp.Contracts` holds the interfaces plugins implement and the attributes that describe them. It depends on `Alcove.Abstractions`, so plugins get it through your contracts.

```xml
<!-- MyApp.Contracts/MyApp.Contracts.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <Version>1.0.0</Version>
    <Description>Extension points of MyApp.</Description>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Alcove.Abstractions" Version="$(AlcoveVersion)" />
    <None Include="build/MyApp.Contracts.props" Pack="true" PackagePath="build/" />
  </ItemGroup>
</Project>
```

```csharp
// MyApp.Contracts/Commands.cs
using Alcove;

namespace MyApp.Contracts;

public interface ICommand
{
    string Run();
}

/// <summary>A command's metadata, readable without creating the command.</summary>
public sealed class CommandAttribute : ExtensionAttribute
{
    public CommandAttribute() : base(typeof(ICommand)) { }

    public required string Name { get; init; }
    public string? Description { get; init; }
}
```

The `build/` props file configures `Alcove.Sdk` in every plugin that references your contracts, so plugin developers can't get the package type wrong:

```xml
<!-- MyApp.Contracts/build/MyApp.Contracts.props -->
<Project>
  <PropertyGroup>
    <PluginPackageType Condition="'$(PluginPackageType)' == ''">MyAppPlugin</PluginPackageType>
  </PropertyGroup>
  <ItemGroup>
    <PluginHostOwnedPackage Include="MyApp.Contracts" />
  </ItemGroup>
</Project>
```

Pack it into your feed: `dotnet pack MyApp.Contracts -o C:\feeds\myapp`. Its version is your compatibility promise: a plugin built against `[1.0, 2.0)` loads into any host that ships 1.x, so a breaking change needs 2.0.

## 2. Create the host

Put the options in one method, because the harness (step 4) reuses them:

```csharp
// MyApp/MyAppHost.cs
using Alcove;
using Alcove.Feeds;
using MyApp.Contracts;

namespace MyApp;

public static class MyAppHost
{
    public static PluginHostOptions CreateOptions(string root) => new()
    {
        Root = root,                                   // absolute; your app decides where
        PluginPackageType = "MyAppPlugin",
        HostOwnedPackages = { "MyApp.Contracts" },     // always the app's copy, never a plugin's
        Feeds = { new PackageFeed(@"C:\feeds\myapp") },
    };

    public static async Task RunAsync(PluginHostOptions options)
    {
        await using var host = PluginHost.Create(options);
        host.PluginFaulted += (_, e) => Console.WriteLine($"! {e.Plugin.Id}: {e.Fault.Message}");

        foreach (var issue in (await host.ValidateAsync()).Issues)
            Console.WriteLine(issue);

        await host.LoadAsync();   // validates, then loads every enabled plugin, each in its own cell

        foreach (var command in host.Catalog.GetAll<ICommand, CommandAttribute>())
            Console.WriteLine($"{command.Metadata.Name}: {command.GetInstance().Run()}");
    }
}
```

```csharp
// MyApp/Program.cs
using MyApp;

var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyApp", "plugins");
await MyAppHost.RunAsync(MyAppHost.CreateOptions(root));
```

```xml
<!-- MyApp/MyApp.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Alcove" Version="$(AlcoveVersion)" />
    <ProjectReference Include="../MyApp.Contracts/MyApp.Contracts.csproj" />
  </ItemGroup>
</Project>
```

`dotnet run --project MyApp` validates an empty plugin root and lists no commands. That's right: nothing is installed yet.

## 3. Install a plugin

Once a plugin package is on your feed (the [plugin quick start](quickstart-plugin-developer.md) makes one), install it:

```csharp
var result = await host.InstallAsync("Contoso.Clock");
Console.WriteLine(result.Status);   // Loaded: it's in the catalog now, no restart
```

Updates, uninstalls and disabling are staged and apply at the next start (`UpdateAsync`, `UninstallAsync`, `DisableAsync`). Who may install plugins, and how, is up to your app: a menu, an admin script, a config file.

## 4. Provide a harness

Plugin developers want F5 to start **your app with their plugin loaded from its build output**, with the debugger attached. That program is your harness. Alcove loads such **development plugins** next to the installed ones, in their own cells, validated as if installed, but it does so only when you add them. Never do that on your app's normal startup path.

The harness is a second exe that reuses your app's code:

```csharp
// MyApp.Harness/Program.cs
using Alcove.Development;
using MyApp;

// A harness root of its own, so plugin developers don't touch their real installation.
var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyApp", "harness-plugins");
var options = MyAppHost.CreateOptions(root);
foreach (var plugin in options.AddDevelopmentPluginsFrom(args, Environment.GetEnvironmentVariables()))
    Console.WriteLine($"Development plugin: {plugin}");   // from --alcove-dev-plugin <folder>

await MyAppHost.RunAsync(options);
```

Pack it so plugin projects can reference it: the exe and everything it needs go under `tools/`, and a `build/` props file tells `Alcove.Sdk` where it is.

```xml
<!-- MyApp.Harness/MyApp.Harness.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <PackageId>MyApp.Harness</PackageId>
    <Version>1.0.0</Version>
    <Description>Runs MyApp with your plugin loaded from its build output.</Description>
    <IncludeBuildOutput>false</IncludeBuildOutput>
    <SuppressDependenciesWhenPacking>true</SuppressDependenciesWhenPacking>
    <DevelopmentDependency>true</DevelopmentDependency>
    <TargetsForTfmSpecificContentInPackage>$(TargetsForTfmSpecificContentInPackage);AddHarnessToPackage</TargetsForTfmSpecificContentInPackage>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../MyApp/MyApp.csproj" />
    <None Include="build/MyApp.Harness.props" Pack="true" PackagePath="build/;buildTransitive/" />
  </ItemGroup>
  <!-- The whole build output, trustedroots/ included, under tools/. -->
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

`dotnet pack MyApp.Harness -o C:\feeds\myapp`. A plugin project that references `MyApp.Harness` with `PrivateAssets="all"` can now be started with `dotnet run`, or with F5 in Visual Studio or Rider, through the launch profiles `Alcove.Sdk` generates. Tell your plugin developers to keep `PrivateAssets="all"`: `DevelopmentDependency` alone doesn't stop the harness from becoming a dependency of their package.

The project reference makes the harness reference your app's exe project. If you'd rather keep the shared code in a library, move `MyAppHost` there and reference that from both.

## 5. Check it end to end

1. Pack `MyApp.Contracts` and `MyApp.Harness` into your feed.
2. Follow the [plugin quick start](quickstart-plugin-developer.md): a plugin project referencing `Alcove.Sdk`, `MyApp.Contracts` and `MyApp.Harness`.
3. `dotnet run` on the plugin project prints `Development plugin: ...` and the plugin's command.
4. `dotnet pack` the plugin into the feed, then `InstallAsync` it from `MyApp`: the same command, now from an installed plugin.

## Next

- Plugin management, cells, trust policy, DI, WPF: the [app developer guide](app-developer-guide.md).
- The harness in depth (both styles, IDE behaviour, what development plugins validate): [§11 of the guide](app-developer-guide.md#11-providing-a-harness).
- Working examples: `samples/ConsoleHost` and `samples/ConsoleHost.Harness` ([samples/README.md](../samples/README.md)).

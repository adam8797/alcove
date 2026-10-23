# Quick start: plugin developers

From nothing to a plugin you can press F5 on, debug, and publish. About fifteen minutes. The [plugin developer guide](plugin-developer-guide.md) has the details behind each step.

The examples use a host app called `MyApp` whose developers publish two packages for you (the [app quick start](quickstart-app-developer.md) builds them):

| Package | What it gives you |
|---|---|
| `MyApp.Contracts` | The extension points you implement (here `ICommand` and `[Command]`), and it configures `Alcove.Sdk` for MyApp |
| `MyApp.Harness` | MyApp, started with your plugin loaded from your build output: what F5 runs |

## 0. Prerequisites

- The .NET 10 SDK.
- An IDE, if you want one: Visual Studio 2026 or Rider (both checked for F5, breakpoints and Hot Reload). VS Code with C# Dev Kit hasn't been checked; `dotnet run` and `dotnet watch` work anywhere.
- The feed where MyApp's packages are. If it isn't nuget.org, add it to a `nuget.config` next to your project:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <add key="myapp" value="https://nuget.example.com/v3/index.json" />
  </packageSources>
</configuration>
```

## 1. Create the project

A plugin is a class library:

```
dotnet new classlib -n Contoso.Clock
```

```xml
<!-- Contoso.Clock/Contoso.Clock.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <PackageId>Contoso.Clock</PackageId>
    <Version>1.0.0</Version>
    <Description>Tells the time in MyApp.</Description>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Alcove.Sdk" Version="0.1.0-dev" PrivateAssets="all" />
    <PackageReference Include="MyApp.Contracts" Version="[1.0,2.0)" />
    <PackageReference Include="MyApp.Harness" Version="1.*" PrivateAssets="all" />
  </ItemGroup>
</Project>
```

- **`Alcove.Sdk`** packs your project as a MyApp plugin: package type, plugin manifest, host-owned packages left out of your output. F5 through a harness needs a version after `0.0.1-alpha`: the newest release, or until there is one, `0.1.0-dev` built from the Alcove repository (`dotnet build -c Release` puts it in `artifacts/packages/Release/`; add that folder as a feed).
- **`MyApp.Contracts`** with a version range: your plugin loads into any MyApp that ships a contracts version in that range.
- **`MyApp.Harness`** with `PrivateAssets="all"`, so it's only for you and never becomes a dependency of your package.

Delete the template's `Class1.cs`.

## 2. Write an extension

```csharp
// Contoso.Clock/ClockCommand.cs
using MyApp.Contracts;

namespace Contoso.Clock;

[Command(Name = "clock", Description = "Tells the time")]
public sealed class ClockCommand : ICommand
{
    public string Run() => $"It's {DateTime.Now:HH:mm}";
}
```

An attributed class is all a simple plugin needs: Alcove finds it and creates it when MyApp asks for commands. For more control (factories, lifetimes, start-up work), add an `IPlugin` entry point ([guide §2](plugin-developer-guide.md#2-contribute-extensions)).

## 3. Run it

```
dotnet run --project Contoso.Clock
```

The harness starts MyApp with your plugin loaded **from `bin/Debug/net10.0/`**, in its own cell, next to the plugins installed in the harness's plugin root. You should see `Development plugin: ...\Contoso.Clock\bin\Debug\net10.0` and your command's output.

The first build also writes `Properties/launchSettings.json` with two launch profiles:

| IDE | Pick the profile | Then |
|---|---|---|
| Visual Studio 2026 | **Contoso.Clock in harness** (the default) | F5 |
| Rider | **Contoso.Clock in harness (Rider)** | Debug. The other profile fails in Rider with "Entry point not found". |
| VS Code, or no IDE | – | `dotnet run`, or `dotnet watch` for Hot Reload |

## 4. Debug

- **Breakpoints** in your plugin just hit: the IDE starts MyApp itself, with the debugger attached from the first line.
- **Hot Reload** (Visual Studio: Alt+F10 or 🔥; Rider: Apply Changes; or `dotnet watch`) patches method bodies in the running app. A change it can't apply, such as changing a class's modifiers, gets an offer to rebuild and restart.
- **Otherwise: stop, edit, F5.** The IDE rebuilds your plugin first. While MyApp runs, Windows keeps your DLL locked, so a rebuild during debugging fails. That's expected.

If MyApp reports something about your plugin, it's in the harness's output with a code you can look up in the [guide's troubleshooting table](plugin-developer-guide.md#troubleshooting-a-development-plugin). The common ones are `DevelopmentDependencyDiffers` (MyApp ships a newer version of one of your dependencies, which you run with) and `PluginDependencyMissing` (your plugin depends on another plugin that isn't installed in the harness's root).

## 5. Publish

```
dotnet pack Contoso.Clock -c Release -o out
dotnet nuget push out/Contoso.Clock.1.0.0.nupkg --source <MyApp's plugin feed>
```

If MyApp requires signed plugins, sign before pushing, and always timestamp:

```
dotnet nuget sign out/Contoso.Clock.1.0.0.nupkg --certificate-path cert.pfx --timestamper http://timestamp.digicert.com
```

Before you push, you can try a real install: copy the `.nupkg` into a local folder feed that MyApp (or its harness) is configured with, and install `Contoso.Clock` from it.

## Next

- Dependencies, cells, DI, WPF plugins, lifecycle: the [plugin developer guide](plugin-developer-guide.md).
- What a development plugin checks, and the harness project alternative: [§7 of the guide](plugin-developer-guide.md#7-run-and-debug-your-plugin).
- A working example: `samples/plugins/Sample.Time` ([samples/README.md](../samples/README.md)).

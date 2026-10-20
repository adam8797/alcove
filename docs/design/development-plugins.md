# Design: development plugins and test harnesses

> Status: **draft for review** (2026-10-20). Decision record: [ADR 0017](../adr/0017-development-plugins.md) (proposed).

## Problem

A plugin developer wants to press F5 and see their plugin running inside the target app (call it **Foo**), with breakpoints working, without packing, pushing to a feed and installing every time. Providing that "harness" mode is Foo's job, but Alcove should make it easy and safe to build.

## Goals

- Load a plugin **straight from its build output** (`bin/Debug/net10.0/`), in its own cell, with the same isolation and validation as in production.
- Load **standard (installed) plugins** alongside development plugins in the same host, so authors can test against real plugins.
- Keep it **opt-in by the app**: Alcove never enables development plugins by itself.
- Support two harness styles: a small **harness project** (option 1) and **F5 on the class library** (option 2).
- Debugging works out of the box: same process, PDBs next to the DLLs.

## Non-goals

- Alcove doesn't ship a Foo-specific harness. App developers build their own `Foo.Harness` package with Alcove's help.
- Edit-and-continue or Hot Reload guarantees (see the spike).
- Development plugins in production. Nothing in the app's normal startup path should load them.

## Terms

| Term | Meaning |
|---|---|
| **Development plugin** | A plugin loaded from a build output folder instead of the plugin root. Not installed, not recorded in `state.json`, not trust-verified. |
| **Harness** | A program, provided by the app developer, that starts the app (or a stand-in host) with development plugins enabled. |

## Design

### 1. Core: a development plugin source

```csharp
options.DevelopmentPlugins.Add(new DevelopmentPlugin(@"..\MyPlugin\bin\Debug\net10.0"));
```

- **Input:** a build output folder containing the plugin assembly, `<AssemblyName>.deps.json` and the plugin manifest (§2). Optionally the path to the `.csproj`, for clearer messages.
- **Identity:** the plugin ID and version come from the manifest the SDK writes into the build output (it records `PackageId` and `Version`).
- **Dependencies:** the plugin's `deps.json` replaces NuGet resolution. Each dependency is classified exactly as at install time (host-owned, host-shared, cell-private, plugin-provided) against the host inventory. Cell-private assemblies load from the build output folder; asset selection already happened at build time (`EnableDynamicLoading` copies RID-specific and native assets).
- **Cells:** same rules as installed plugins (app options → manifest request → own cell).
- **Validation:** runs as usual, plus development-specific checks:
  - a host-owned dependency the host doesn't satisfy → error, just as it would be after a real install;
  - a dependency resolved differently from how an install would resolve it (e.g. the host has a newer host-shared version) → warning, because production would behave differently.
- **Not done for development plugins:** no state file entries, no trust policy, no hash pinning, no cleanup, no staged changes. `PluginInfo` gets an `IsDevelopment` flag so the app can show it.
- **Mixing with installed plugins:** both load in one `LoadAsync`. If an installed plugin has the same ID, the development plugin wins for this run, with a warning; the installed one is left untouched on disk.
- **Plugin-to-plugin dependencies:** a development plugin may depend on another development plugin or on an installed plugin; normal cross-cell rules apply.
- **File locks:** on Windows a running host locks the DLLs it loaded, so rebuilding during a session fails. Option `ShadowCopy` (default on) copies the build output to a per-run temp folder under the plugin root's `.dev/` folder before loading, so the author can rebuild while the harness runs (the new build is picked up at the next start).

### 2. SDK: make a build output loadable

`Alcove.Sdk` additionally, on every build:

- writes `<PackageId>.plugin.json` into the build output, with `packageId` and `version` added to the manifest (schema stays backward compatible: the fields are optional and ignored for installed plugins);
- sets `EnableDynamicLoading=true` unless the project sets it, so dependencies (including native and RID-specific assets) are copied to the output, and host-owned packages stay excluded (already the case today).

### 3. The opt-in convention

Alcove provides parsing, the app decides:

```csharp
// In Foo's harness entry point only:
options.AddDevelopmentPluginsFrom(args, Environment.GetEnvironmentVariables());
```

- Reads `--alcove-dev-plugin <path>` (repeatable) and the `ALCOVE_DEV_PLUGINS` environment variable (paths separated by `Path.PathSeparator`).
- Does nothing unless the app calls it, so production code paths stay clean. The docs recommend calling it only from a harness build or behind an explicit flag.

### 4. Harness style 1: a harness project

The plugin author adds a small exe project:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0-windows</TargetFramework></PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Foo.Harness" Version="..." />
    <PluginUnderTest Include="..\MyPlugin\MyPlugin.csproj" />
  </ItemGroup>
</Project>
```

```csharp
await Foo.Harness.FooHarness.RunAsync(args);
```

`PluginUnderTest` comes from `Alcove.Sdk`'s build targets (`Alcove.Sdk` becomes a dependency of `Foo.Harness`, or the harness project references it directly):

- adds a `ProjectReference` with `ReferenceOutputAssembly="false"` and `Private="false"`, so the plugin is **built first but never copied into the harness or loaded into the default context**;
- generates an assembly attribute (`[assembly: AlcoveDevelopmentPlugin(@"<plugin output path>")]`) that `AddDevelopmentPluginsFrom` also reads, so no command-line arguments are needed.

### 5. Harness style 2: F5 on the class library

An app developer can make `Foo.Harness` a package that ships Foo (or a stand-in host) under `tools/` plus `buildTransitive` targets. A plugin project that references `Foo.Harness` then gets:

- `RunCommand` = the harness executable, and `RunArguments` = `--alcove-dev-plugin "$(TargetDir)"`. Verified: `dotnet run` honours these for a class library (2026-10-20).
- For Visual Studio, which won't start a class library without a launch profile: a generated `Properties/launchSettings.json` "Executable" profile (only if the project has none), or documented manual steps.

Alcove supplies the reusable parts: an `Alcove.Sdk` property (`AlcoveHarnessExecutable`) that sets `RunCommand`/`RunArguments` and generates the launch profile, so each app's harness package needs only a few lines.

### 6. What the app developer writes

- **Option 1:** a `Foo.Harness` library package with a `RunAsync(args)` that builds Foo's normal `PluginHostOptions` (root, feeds, host-owned packages), calls `AddDevelopmentPluginsFrom(args, env)`, and starts Foo's UI. It references `Alcove.Sdk` so `PluginUnderTest` is available.
- **Option 2:** the same, packaged with Foo's executable and setting `AlcoveHarnessExecutable`.
- **Installed plugins:** the harness uses a plugin root of its choosing, typically a separate "harness" root so authors don't touch their real installation, which is the app's decision.

## Spike first (before building §5)

1. F5 behaviour per IDE for a class library with `RunCommand` / a generated launch profile: Visual Studio 2026, Rider, VS Code (C# Dev Kit), CLI.
2. Breakpoints in a development plugin loaded into a cell (expected to work).
3. Hot Reload and edit-and-continue for assemblies in a non-default `AssemblyLoadContext`, with and without shadow copying.

## Implementation outline

1. Core: `DevelopmentPlugin`, `PluginHostOptions.DevelopmentPlugins`, deps.json-based classification, validation checks, shadow copy, `PluginInfo.IsDevelopment`, mixing with installed plugins.
2. SDK: manifest and dynamic-loading output on build; `PluginUnderTest`; generated attribute; `AlcoveHarnessExecutable`.
3. `AddDevelopmentPluginsFrom(args, env)` helper and the assembly-attribute reader.
4. Sample: `samples/ConsoleHost.Harness` (option 1) plus a sample plugin wired to it; integration tests building a plugin and loading it from `bin/` together with installed plugins.
5. The spike, then option 2 support.
6. Docs: plugin developer guide ("Testing your plugin"), app developer guide ("Providing a harness").

## Open questions

- Hot Reload: required, or is rebuild-and-restart enough? (The spike will show what's possible.)
- Should `ShadowCopy` default to on for every OS, or only Windows (Linux and macOS don't lock loaded files)?
- Should the dev plugin's dependency warnings (resolved differently from a real install) be errors under a strict option?

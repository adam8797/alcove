# Design: development plugins and test harnesses

> Status: **accepted** (2026-10-20, after review), **implemented** (2026-10-20; the IDE parts of the spike are still to be run, see the [checklist](#ide-checklist)). Decision record: [ADR 0017](../adr/0017-development-plugins.md).

## Problem

A plugin developer wants to press F5 and see their plugin running inside the target app (call it **Foo**), with breakpoints working, without packing, pushing to a feed and installing every time. Providing that "harness" mode is Foo's job, but Alcove should make it easy and safe to build.

## Goals

- Load a plugin **straight from its build output** (`bin/Debug/net10.0/`), in its own cell, with the same isolation and validation as in production.
- Load **standard (installed) plugins** alongside development plugins in the same host, so authors can test against real plugins.
- Keep it **opt-in by the app**: Alcove never enables development plugins by itself.
- Support two harness styles: a small **harness project** (option 1) and **F5 on the class library** (option 2).
- **The plugin developer forgets the plugin is hosted in another app.** They press F5 on the plugin project and the app launches, attached to the debugger, exactly as if they were developing a standalone app. To change code they stop debugging (which closes the app), rebuild and press F5 again: the normal app-development loop.
- Debugging works out of the box: same process, PDBs next to the DLLs.

## Non-goals

- Alcove doesn't ship a Foo-specific harness. App developers build their own `Foo.Harness` package with Alcove's help.
- Edit-and-continue or Hot Reload guarantees. Not required, but very nice to have: the spike checks what works.
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
  - a dependency resolved differently from how an install would resolve it (e.g. the host has a newer host-shared version) → **warning** (decided), because production would behave differently.
- **Not done for development plugins:** no state file entries, no trust policy, no hash pinning, no cleanup, no staged changes. `PluginInfo` gets an `IsDevelopment` flag so the app can show it.
- **Mixing with installed plugins:** both load in one `LoadAsync`. If an installed plugin has the same ID, the development plugin wins for this run, with a warning; the installed one is left untouched on disk.
- **Plugin-to-plugin dependencies:** a development plugin may depend on another development plugin or on an installed plugin; normal cross-cell rules apply.
- **Loaded in place, no copying.** Development plugins load straight from the build output. On Windows the running app locks those DLLs, just as any app being debugged locks its own; that's the intended loop (stop debugging, rebuild, F5).

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

### 4. Harness style 1: a harness project (supported; style 2 is the primary experience)

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

### 5. Harness style 2: F5 on the class library (the primary experience)

An app developer can make `Foo.Harness` a package that ships Foo (or a stand-in host) under `tools/` plus `buildTransitive` targets. A plugin project that references `Foo.Harness` then gets:

- `RunCommand` = the harness executable, and `RunArguments` = `--alcove-dev-plugin "$(TargetDir)"`. Verified: `dotnet run` honours these for a class library (2026-10-20).
- For Visual Studio and Rider, which won't start a class library without a launch profile: a generated `Properties/launchSettings.json` (only if the project has none) with two profiles holding the harness command line, a "Project" one for VS (it has no Hot Reload for "Executable" profiles) and an "Executable" one for Rider (it runs the class library itself for a "Project" profile). Windows results below.

In both IDE paths the IDE starts the harness executable itself, so its debugger is attached from the first line and breakpoints in the plugin just hit; no manual "attach to process".

Alcove supplies the reusable parts: an `Alcove.Sdk` property (`AlcoveHarnessExecutable`) that sets `RunCommand`/`RunArguments` and generates the launch profile, so each app's harness package needs only a few lines.

### 6. What the app developer writes

- **Option 1:** a `Foo.Harness` library package with a `RunAsync(args)` that builds Foo's normal `PluginHostOptions` (root, feeds, host-owned packages), calls `AddDevelopmentPluginsFrom(args, env)`, and starts Foo's UI. It references `Alcove.Sdk` so `PluginUnderTest` is available.
- **Option 2:** the same, packaged with Foo's executable and setting `AlcoveHarnessExecutable`.
- **Installed plugins:** the harness uses a plugin root of its choosing, typically a separate "harness" root so authors don't touch their real installation, which is the app's decision.

## Spike first (before building §5)

1. F5 behaviour per IDE for a class library with `RunCommand` / a generated launch profile: Visual Studio 2026, Rider, VS Code (C# Dev Kit), CLI.
2. Breakpoints in a development plugin loaded into a cell (expected to work).
3. Hot Reload and edit-and-continue for assemblies in a non-default `AssemblyLoadContext`, launched from each IDE.

## Implementation outline

1. Core: `DevelopmentPlugin`, `PluginHostOptions.DevelopmentPlugins`, deps.json-based classification, validation checks, ~~shadow copy~~ (dropped in review), `PluginInfo.IsDevelopment`, mixing with installed plugins.
2. SDK: manifest and dynamic-loading output on build; `PluginUnderTest`; generated attribute; `AlcoveHarnessExecutable`.
3. `AddDevelopmentPluginsFrom(args, env)` helper and the assembly-attribute reader.
4. Sample: `samples/ConsoleHost.Harness` (option 1) plus a sample plugin wired to it; integration tests building a plugin and loading it from `bin/` together with installed plugins.
5. The spike, then option 2 support.
6. Docs: plugin developer guide ("Testing your plugin"), app developer guide ("Providing a harness").

## Decided in review (2026-10-20)

- **Hot Reload:** not required; the spike checks it because it would be very nice.
- **No shadow copy:** development plugins load in place; the stop-rebuild-F5 loop is the intended one.
- **Dependency differences** from a real install: warnings.

## Implementation notes (2026-10-20)

Where the implementation had to decide something the design left open:

- **API.** `Alcove.Development`: `DevelopmentPlugin(outputDirectory, projectPath?)`, `AlcoveDevelopmentPluginAttribute`, and the extension method `options.AddDevelopmentPluginsFrom(args, environment)` (returns what it added; `CommandLineOption` and `EnvironmentVariable` constants). In the root: `PluginHostOptions.DevelopmentPlugins` (code only, `[JsonIgnore]`) and `PluginInfo.IsDevelopment`. Manifest: optional `packageId` and `version` (`PluginManifest.PackageId`, `.Version`). Validation codes: `DevelopmentPluginInvalid`, `DevelopmentPluginOverridesInstalled`, `DevelopmentDependencyDiffers`. Log events 2400–2401 (`DevelopmentLog`).
- **Where it plugs in.** The validator reads the development plugins (`DevelopmentPluginReader`) into would-be state entries that are never written, puts them next to the state file's plugins (replacing one with the same ID), and gives the classifier their asset locations in the build output. Everything after (cells, order, activation) is unchanged. Installs, updates and staging never see them; an install of a plugin with a running development plugin's ID is `PendingRestart`.
- **Classification from `deps.json`.** The closure from the plugin's project, not walking into plugin dependencies (a library whose ID is an installed or development plugin) or host-owned packages. Host-shared when the host has the package at the built version or higher (a higher one warns: `DevelopmentDependencyDiffers`); cell-private otherwise, or when the manifest says so. Assets: runtime and native files flat in the output folder, satellites in their culture folder, `runtimeTargets` under `runtimes/<rid>/`, the best RID for the host as the .NET host picks them. A listed file missing from the output is an error for that plugin.
- **Host-owned conflicts.** `Alcove.Sdk` keeps host-owned packages out of the build output, so the plugin's `deps.json` doesn't list them (e.g. the contracts). The gate is therefore the plugin's own assembly references: a reference to a host-owned assembly with a higher version than the host's is `HostOwnedConflict`, one the host doesn't have is `HostOwnedMissing`. Host-owned packages that are in the `deps.json` go through the classifier's usual check.
- **SDK.** An `Exe`/`WinExe` project referencing `Alcove.Sdk` is a harness, not a plugin (`AlcoveIsPlugin=false`): no plugin targets, no host-owned exclusion. Plugins get `EnableDynamicLoading=true` and the manifest copied into `$(OutDir)` (a `Copy`, so it doesn't flow into referencing projects). `PluginUnderTest` becomes a `ProjectReference` with `ReferenceOutputAssembly=false`, `Private=false` and `ExcludeAssets=all` (so the plugin's packages don't flow into the harness either); because the first restore runs before package targets are imported, a plugin under test that was never restored is restored by the harness build. The launch profile is written by a small inline task: MSBuild turns the backslashes of JSON escapes into slashes in item specs on Unix. The build output path is passed without its trailing separator (`"C:\out\"` would escape its closing quote on Windows).
- **`dotnet run` with the generated profile.** `dotnet run` (and `dotnet watch`) say "Using launch settings from ..." but still run `RunCommand`/`RunArguments`; `dotnet watch` takes the profile's `commandLineArgs`, so after changing `AlcoveHarnessArguments`, delete the generated `launchSettings.json` (it's only generated when missing).

## Spike results

Run on Linux, .NET SDK 10.0.203, with `samples/ConsoleHost.Harness` and `samples/plugins/Sample.Time` (see samples/README.md).

| Question | Result |
|---|---|
| CLI: `dotnet run` on a class library with `RunCommand` | **Works** (also covered by `HarnessTests`). Starts the harness with the plugin's build output; installed plugins load next to it. |
| CLI: Hot Reload, style 2 (`dotnet watch --project samples/plugins/Sample.Time`, harness running with `--repeat`) | **Works.** Editing a method body of `TimeCommand.Run` while the harness ran: `dotnet watch 🔥 [Sample.Time (net10.0)] Hot reload succeeded.`, and the next run of the command printed the new string, in the same process (the harness's repeat counter kept counting: 2/12 old text, 3/12 new). Reverting applied the same way. The plugin is in its own cell (a non-default `AssemblyLoadContext`, loaded from its path). |
| CLI: Hot Reload, style 1 (`dotnet watch --project samples/ConsoleHost.Harness`, editing `Sample.Greetings`, its `PluginUnderTest`) | **Works** the same way: `dotnet watch` watches the plugin project through the reference (`Hot reload succeeded` for `ConsoleHost.Harness`), and the plugin loaded in its cell from `bin/` ran the new code. |
| Rude edits (e.g. new members) | Not tried; `dotnet watch` offers a restart then, which is the normal loop. |
| Breakpoints in a development plugin | Not testable here (no IDE debugger on this machine); expected to work (same process, PDBs next to the DLLs, loaded from the path). |
| F5 in Visual Studio 2026, Rider, VS Code | Not testable here: see the checklist. |

### IDE checklist

For someone with Visual Studio 2026, Rider and VS Code (C# Dev Kit), on Windows. Prepare once: run `samples/run-harness-sample.sh` from Git Bash (or the steps in it by hand), so the sample plugins are packed and installed into `artifacts/sample/plugin-root`. Open `samples/plugins/Sample.Time/Sample.Time.csproj` (and, for style 1, `samples/ConsoleHost.Harness/ConsoleHost.Harness.csproj`); restore needs `AlcoveVersion` and `RestoreConfigFile` (set them as environment variables, as in samples/README.md, before starting the IDE). Record for each IDE: works / doesn't / what you saw.

1. **Launch profile.** After the first build of `Sample.Time`, `Properties/launchSettings.json` exists with the profiles "Sample.Time in harness" (`commandName: Project`) and "Sample.Time in harness (Rider)" (`Executable`); before 2026-10-21, only an `Executable` one. The IDE lists it as a start profile.
2. **F5 on the class library (style 2).** Pressing F5 (Debug) on `Sample.Time` starts `ConsoleHost.Harness` under the debugger, with no "a class library can't be started" error and no manual attach. Its console shows `Sample.Time ... development ... Loaded`.
   - Rider: if it ignores the profile, try its "Run/Debug configuration: .NET Launch Settings Profile", and note whether it picks up `RunCommand` on its own (its ".NET Project" configuration).
   - VS Code: C# Dev Kit, "Run and Debug" → C#: does it offer the launch profile for a class library? If not, note it; a `launch.json` `coreclr` configuration with `program: dotnet`, `args: [<harness dll>, "--alcove-dev-plugin", "<bin>"]` is the fallback to try.
3. **Breakpoint.** A breakpoint in `TimeCommand.Run` hits (it runs during startup; with `--repeat 30` in `AlcoveHarnessArguments`, it hits every 2 seconds). Locals and call stack show; the module window lists `Sample.Time.dll` from `samples/plugins/Sample.Time/bin/Debug/net10.0/`.
4. **Stop and rebuild.** Stop debugging (the harness exits), change the string in `TimeCommand.Run`, F5 again: the new text shows. While debugging, a rebuild fails because `Sample.Time.dll` is locked (expected on Windows).
5. **Hot Reload.** With `--repeat 30`, while debugging, change the string in `TimeCommand.Run` and apply Hot Reload (VS: the flame button / Alt+F10; Rider: "Apply changes"; VS Code: Hot Reload in C# Dev Kit). Does the next run print the new text without a restart? Edit-and-continue at a breakpoint: change the line, continue.
6. **Style 1.** Same as 2–5 with F5 on `ConsoleHost.Harness` (a normal exe) and a breakpoint in `samples/plugins/Sample.Greetings/GreetCommand.cs`.
7. **`dotnet watch` on Windows.** `dotnet watch --project samples/plugins/Sample.Time` with `--repeat 30` (delete the generated `launchSettings.json` after adding it to `AlcoveHarnessArguments`): Hot Reload as on Linux?

### Windows results: Visual Studio 2026 (2026-10-21)

Visual Studio 2026 Community 18.11, .NET SDK 10.0.402, Windows 11; the checklist above, run by hand (`--repeat 30`, repeat delay raised to 10 s for the Hot Reload edits). Style 2 was tried with three launch profiles; the generator now writes the second.

| Check | Style 2, `Executable` profile | Style 2, `Project` profile with `commandLineArgs` | Style 2, bare `Project` profile | Style 1 (`ConsoleHost.Harness`) |
|---|---|---|---|---|
| Profile picked up | Yes | Yes | Yes | Yes |
| F5 | Works: `dotnet` from `PATH` starts the harness | Works: VS starts `RunCommand` (`dotnet`) with the profile's arguments | **Fails**: `dotnet.exe` without the harness, exit code `0x80008081` (VS doesn't run the target that sets `RunArguments`) | Works |
| Breakpoint | Hits; locals, call stack, eval; module from `bin/`, `IsDevelopment` true | Hits | – | Hits in `GreetCommand.Run`; Modules: `samples\plugins\Sample.Greetings\bin\Debug\net10.0\Sample.Greetings.dll` |
| Stop, edit, F5 | New code runs | – | – | VS rebuilds Sample.Greetings (a `PluginUnderTest`), new code runs |
| Rebuild while debugging | Fails, `Sample.Time.dll` locked (MSB3026), as expected | – | – | – |
| Hot Reload | **Fails**: "Hot Reload is not supported in this session due to the lack of a managed language service." then "An unexpected error has occurred" (a modal dialog) | **Works**: "Code changes were successfully applied", the next run prints the new text | – | **Works** in the plugin's cell: "Code changes were successfully applied" |
| Edit and Continue at a breakpoint | **Fails**: not applied; the next hit warns that the source differs | – | – | – |
| Rude edit (`sealed` removed) | – | ENC0004 "Updating the modifiers of class requires restarting the application"; rebuild-and-restart brings the harness back with the change | – | Same ENC0004; restart works (VS also logs "An unexpected error has occurred, any pending updates have been discarded") |

So VS gives Hot Reload to the plugin in its cell when the session belongs to a project: a `Project` profile, or the harness project itself. A `Project` profile on the class library needs the harness command line in `commandLineArgs`, which the generator writes. An automated run (DTE) the night before hung with the `Project` profile, the injected Hot Reload agent waiting for VS; it didn't happen by hand, so it's put down to the automation.

### Windows results: Rider (2026-10-21)

Rider on Windows 11, the same machine and checklist, style 2:

| Profile | Result |
|---|---|
| `Project` with `commandLineArgs` (VS's) | **Fails**: Rider runs the class library itself, `System.MissingMethodException: Entry point not found in assembly 'Sample.Time'` |
| `Project` with `commandLineArgs` and `executablePath: dotnet` | **Fails** the same way: Rider ignores `executablePath` for a `Project` profile |
| `Executable` (`dotnet` with the harness command line) | **Works**: the breakpoint hits, and Hot Reload applies the edit to the plugin in its cell |

No one profile works in both IDEs, so the generator writes two: "*project* in harness" (`Project`, first, so it's VS's default) and "*project* in harness (Rider)" (`Executable`). Both were then checked as generated: breakpoint and Hot Reload work in VS with the first and in Rider with the second. Rider lists both (and keeps run configurations from profiles that no longer exist).

Style 1 in Rider (`ConsoleHost.Harness`, a normal exe, breakpoint in `GreetCommand.Run`): the breakpoint hits, Hot Reload applies in the plugin's cell, and after stop, edit, Debug, Rider rebuilds Sample.Greetings (a `PluginUnderTest`) and the new code runs.

VS Code wasn't checked.

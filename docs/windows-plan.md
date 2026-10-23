# Windows continuation plan

*Written 2026-10-20 for an agent working on a Windows machine. Everything else so far was developed on Linux; the items below need Windows.*

## Before you start

1. **Read:** [docs/status.md](status.md), [CONTEXT.md](../CONTEXT.md) (terms: cells, plugin root, host-owned, ...), [CONTRIBUTING.md](../CONTRIBUTING.md) (conventions, guard-rail tests, releasing), and the docs each task links to.
2. **Toolchain:** the .NET SDK pinned in `global.json` (10.0.203 or a later 10.0 feature band). Visual Studio 2026, JetBrains Rider and VS Code with C# Dev Kit for task 2.
3. **Build and test once:** `dotnet build Alcove.slnx` (zero warnings expected) and `dotnet test Alcove.slnx`. On Windows the 6 WPF runtime scenarios in `tests/Alcove.Wpf.Tests` run instead of being skipped. Record the pass/skip counts.
4. **Rules:**
   - Commit locally after each task with a **one-line subject only**: no body, no `Co-Authored-By` or other trailers.
   - **Don't push, tag or publish** without asking the user. A pushed `v*` tag publishes to nuget.org, and that's permanent.
   - Record results in the docs named below. A task that finds something broken: fix it if it's in scope and small, otherwise write it up under "Findings" in this file.
   - Don't edit `spike/` reports except to append a dated "Windows results" section, or `docs/reviews/`.

## Tasks

### 1. Full suite on Windows (baseline)
- Run `dotnet test Alcove.slnx` twice. Note durations: the CI Windows job's integration tests take about 7 minutes against about 25 seconds on Linux.
- **Investigate the slowness:** per-test `dotnet build`/`pack` of fixtures? Defender scanning `%TEMP%`? Process start cost? Propose speedups, and implement them if they're safe: e.g. pack fixtures once per run, reuse one `NUGET_PACKAGES`, or document a Defender exclusion for the test temp folder.
- **Record:** a "Windows test baseline" note in [docs/status.md](status.md).

### 2. Development plugins in the IDEs (main task)
The feature: [docs/design/development-plugins.md](design/development-plugins.md), with the user-facing sections in the plugin and app developer guides ("Testing your plugin", "Providing a harness"). The goal: a plugin developer presses F5 on the plugin project and the app launches under the debugger, as if they were developing a standalone app.
- **Follow the design doc's "IDE checklist"** in each of **Visual Studio 2026, Rider and VS Code**, for both samples:
  - style 2: `samples/plugins/Sample.Time`, F5 on the plugin project;
  - style 1: `samples/ConsoleHost.Harness`.
- **For each IDE and sample, check:**
  - F5 launches the host with the plugin loaded (`PluginInfo.IsDevelopment`), with no manual attach;
  - a breakpoint in plugin code hits;
  - stop → rebuild → F5 works (no stale locked files);
  - Hot Reload of a method-body edit applies to the plugin running in its cell (it worked with `dotnet watch` on Linux);
  - what happens on a rude edit (e.g. adding a field).
- **Visual Studio specifics:** whether the generated `Properties/launchSettings.json` "Executable" profile is picked up as is, and whether VS needs anything else. If it needs a different profile shape, fix the generator in `Alcove.Sdk`.
- **Record** a results table (IDE × sample × check) in the design doc's spike results. If everything works, mark the spike done in [docs/status.md](status.md).

### 3. WPF sample by hand
- Run `samples/run-wpf-sample.ps1`.
- **Check:**
  - the host window opens and shows the plugin panels with the app's styles (`WpfHost.Sdk`);
  - `Sample.Notes` shows its view via the view-model mapping;
  - `Sample.Charts` renders (it uses `Sample.Wpf.Controls` only from XAML, so it depends on pre-loading);
  - the hot-install button loads `Sample.Clock` without a restart, and it renders while sharing `Sample.Wpf.Controls` with Charts from another cell (ADR 0015).
- **Also try a development plugin in the WPF host**, if the sample supports it or you can add it cheaply (`--alcove-dev-plugin <bin path>`).
- **Record** the results in `samples/README.md` (a "Verified on Windows" note) and fix anything broken.

### 4. Signature verification on Windows (last Phase 0b item)
- Spike: [spike/nuget-signature-verification/REPORT.md](../spike/nuget-signature-verification/REPORT.md). Re-create its inputs on Windows. Its `setup.sh` needs bash + openssl (Git Bash works), or create the self-signed, tampered and unsigned packages another way.
- Run the `Verifier matrix` with the bundled roots and compare the verdicts with the Linux results in its `results/` folder.
- **Also check the default Windows trust store without the bundle:** the report predicted it accepts nuget.org packages, which would show the bundle isn't strictly needed on Windows (the library ships it anyway, for identical behaviour everywhere).
- **Network tests:** run `ALCOVE_NETWORK_TESTS=1 dotnet test tests/Alcove.IntegrationTests --filter Category=Network`.
- **Record:** append a dated "Windows results" section to the spike report, and update ADR 0010's amendments and [docs/open-questions.md](open-questions.md) ("Signature verification on Windows" → Resolved).

### 5. Native library name collisions (optional)
- [docs/open-questions.md](open-questions.md) asks whether two cells with different versions of the same native dependency end up sharing one on Windows; on Linux it happens by soname. The integration tests skip the native scenarios on Windows, because the fixtures are built with gcc.
- **If cheap:** build the two native fixture versions with MSVC or MinGW and run the hot-load spike's native scenarios. **Record** the outcome in open-questions.

### 6. Known gaps from the development-plugins work (any OS, optional)
Fix these if time allows, each with a test:
- a `deps.json` dependency on a plugin that is neither installed nor a development plugin is treated as cell-private instead of as a missing plugin dependency;
- installs don't see development plugins, so an installed plugin depending on one isn't resolved against it;
- an unidentifiable build folder's validation issue uses the folder path as its `PluginId`.

## Reporting back
When done, update [docs/status.md](status.md) and add a short summary under "Findings" below. That summary should cover each task's result, what you fixed (commit hashes), and anything that needs the user's decision.

## Findings

### Task 1: test baseline (2026-10-20)

Green, zero warnings; the whole suite went from 2 min 27 s to 1 min 16 s on this machine. Details in [status.md](status.md) ("Windows test baseline").

**Needs your decision: NuGet's chain build retries cost apps 3 s per package on Windows.** NuGet.Packaging (7.9.0) retries a chain that ends in `UntrustedRoot` 3 times, 1 s apart, on Windows only (its `ChainBuildPolicy`; override: `NUGET_EXPERIMENTAL_CHAIN_BUILD_RETRY_POLICY=<retries>,<sleep ms>`). Reproduced outside Alcove with NuGet alone: `SignatureTrustAndValidityVerificationProvider` takes 3.0 s per verification of a package signed with a self-signed certificate, with online or offline revocation alike, against 20 ms with `0,0`; the verdict codes are the same. A plain `X509Chain.Build` of the same certificate takes under 20 ms. So in an app, each install or verification of a package whose signature chains to a root Windows doesn't trust (self-signed authors, `AllowUntrustedRoot` signers, also accepted with warnings by the default policy) takes 3 s longer. Packages chaining to a trusted root (nuget.org's) aren't affected. Alcove can't change it without setting a process-wide environment variable, which ADR 0012 rules out. Options: document it in the app developer guide (an app may set the variable itself), or leave it.

### Task 2: development plugins in the IDEs (2026-10-21)

Visual Studio 2026 (18.11) and Rider, both harness styles, checked by hand; VS Code left out at the user's request. Results tables in the [design doc](design/development-plugins.md#windows-results-visual-studio-2026-2026-10-21).

- **Style 1** (`ConsoleHost.Harness`, an exe with `PluginUnderTest`): works in both. F5, breakpoints in the plugin (loaded from its `bin/` into its cell), Hot Reload applied in the cell, rude edits offer rebuild and restart, and F5 after an edit rebuilds the plugin under test.
- **Style 2** (F5 on the class library): the generated `Executable` profile gave F5 and breakpoints in VS but **no Hot Reload or Edit and Continue** ("Hot Reload is not supported in this session due to the lack of a managed language service"). A `Project` profile carrying the harness command line works in VS, Hot Reload included, but **Rider runs the class library itself for any `Project` profile** ("Entry point not found"), and works only with `Executable`. A bare `Project` profile fails in VS (it doesn't run the target that sets `RunArguments`). **Fixed:** the generator now writes two profiles, "*project* in harness" (`Project`, VS's default) and "*project* in harness (Rider)" (`Executable`); both were checked as generated, breakpoint and Hot Reload, in their IDE. `dotnet run` is unchanged. (4b0c577, d9ed4dd, 6e5246e)
- Also fixed: the sample scripts under Git Bash (`pwd -W`, 311f81f).

### Task 3: WPF sample (2026-10-21)

Everything the plan listed works: panels with the app's styles, Notes through the view-model mapping, Charts (pre-loading), the accent colour switch, hot-installing Clock (sharing `Sample.Wpf.Controls` with Charts), and with `-KeepRoot` all three load at startup. **Fixed:** closing the window crashed the host (`Close()` called inside its own `Closing` event when disposal completed synchronously). A development plugin works in the WPF host too (Clock from its `bin/`, checked with a temporary `AddDevelopmentPluginsFrom`; the sample doesn't keep it). Recorded in [samples/README.md](../samples/README.md). (486201d)

### Task 4: signature verification (2026-10-21)

The `Verifier matrix` gives the same verdicts and error codes as the Linux bundled run under every policy, with or without the bundle and with either trust store setting: on Windows NuGet uses the Windows trust store and ignores the bundle, and that store accepts nuget.org packages. Network tests: 7 passed. Untrusted-root signatures take ≈3 s each (the chain build retries above). Recorded in the [spike report](../spike/nuget-signature-verification/REPORT.md#windows-results-2026-10-21), ADR 0010 and open-questions (resolved). (84fb95f)

### Task 5: native library collisions (2026-10-21)

Built the hot-load spike's native libraries with MSVC (in a copy outside the repo): Windows behaves as Linux, by module name instead of soname. A DLL's import resolved to the same-named DLL already loaded for another context, not the one next to it; the existing collision warning stands. **Also:** the integration tests now build their native test library with MSVC on Windows (found with vswhere) and run the native cell scenarios there instead of skipping them (one test's cleanup fixed for Windows file locks). Recorded in the [spike report](../spike/hot-load-into-loaded-compartment/REPORT.md#windows-results-2026-10-21), architecture §4 and open-questions (resolved). (e7b4d18)

### Task 6: development-plugin gaps (2026-10-21)

- **Fixed: a plugin dependency that's neither installed nor a development plugin.** A `deps.json` can't tell a plugin from a library, so `Alcove.Sdk` now records the plugin's plugin dependencies in the manifest (`pluginDependencies`, optional, ignored for installed plugins): project references to plugin projects (their target path item carries `AlcovePluginId`) and package references whose nuspec has the host's plugin package type. The reader classifies those as plugin dependencies, so a missing one is `PluginDependencyMissing`, as for an install, instead of the plugin loading the copy in its `bin/`. Tests for both reference kinds. `PluginManifest.PluginDependencies` is new public API in Alcove.Abstractions. (c9180f5)
- **Needs your decision: installs don't see development plugins.** An install of X that depends on plugin P, while P runs as a development plugin, still resolves P from the feeds: it installs P too and is `PendingRestart` ("The development plugin P is loaded in this process instead"; checked with the Hosting fixtures), or fails if P isn't on a feed. Resolving X against the development P instead would record a dependency on a plugin that isn't installed, so the next start without the development plugin would report X's dependency missing. Options: (1) leave it: installs are about the plugin root, which stays self-consistent; document it in the plugin developer guide; (2) let installs resolve against development plugins and accept that the root depends on them. I recommend (1).
- **Needs your decision: an unidentifiable build folder's issue uses the folder path as its `PluginId`** (or the project's name, when the harness knows the project). An issue without a `PluginId` is global, and a global error stops anything from loading; a guessed ID would block an installed plugin with that ID. Options: (1) leave it and document that `PluginId` can be a folder path for `DevelopmentPluginInvalid`; (2) make it a warning without a `PluginId`, so the folder doesn't look like a plugin (but a broken folder would no longer be an error); (3) add a `Source` (or `Path`) field to `ValidationIssue` for issues about something that isn't a plugin. I recommend (1) now, (3) if more such issues appear.

### Summary of decisions for you

1. NuGet's chain build retries on Windows: document for app developers, or leave (task 1).
2. Installs and development plugins (task 6): leave and document, or resolve against development plugins.
3. `PluginId` for unidentifiable build folders (task 6): leave and document, a global warning, or a new field.
4. VS Code checks: still open, whenever you want them.

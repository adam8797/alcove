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

*(to be filled in on Windows)*

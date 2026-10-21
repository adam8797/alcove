# Status

*Last updated 2026-10-19.*

## Done

| Phase | What | Notes |
|---|---|---|
| 0 | Spikes | All five ran on Linux; the WPF spike also ran on Windows (2026-10-19): all predictions matched except 7.4, which was better than predicted. |
| 1 | Repository foundation | Solution, central package management, GitHub Actions CI. |
| 2 | `Alcove.Abstractions`, `Alcove.Sdk` | Manifest generation, explicit `AssemblyVersion`, host-owned packages, `ALCOVE001`–`006`. |
| 3 | Offline core | Cells (load order, answer freezing, version checks, natives, satellites), exposed-dependency scanner, WPF metadata analysis, validation, activation, extension catalog, `PluginHost`, console sample. |
| 4 | Feeds and install | Search, `RemoteDependencyWalker` resolution (matches `dotnet restore`), download, `InstallAsync` with the hot-load rule, trust policy and signature verification with the shipped root bundle, localhost test feed. |
| 5 | Lifecycle | Staged update/uninstall/enable/disable/rollback, startup apply and cleanup, in-use records, pending changes, update checks, restore, verify. |
| 6 | DI add-ons | `Alcove.DependencyInjection` and `Alcove.Autofac`, one service provider per cell, shared test suite. |
| 7 | WPF add-on | `Alcove.Wpf`: `UseWpf` (pre-loading, resource merging, opt-in default-context forwarder and view-model → view mapping), `DispatcherExtensionCatalog`, `PluginPackUri`, `SharedResourceKey<T>`, `ViewTemplates`. Core hook `ICellObserver`. WPF sample (`samples/WpfHost`). The WPF runtime scenarios run in the Windows CI job and haven't run yet (not pushed). |
| — | Development plugins | [ADR 0017](adr/0017-development-plugins.md): load a plugin from its build output next to installed plugins (`PluginHostOptions.DevelopmentPlugins`, `AddDevelopmentPluginsFrom`), `Alcove.Sdk` manifest in `bin/`, `PluginUnderTest`, `AlcoveHarnessExecutable`; sample `samples/run-harness-sample.sh`. Hot Reload works with `dotnet watch` on Linux; the IDE checks are [waiting on you](#waiting-on-you). |
| 8 | Hardening | Public API snapshots, XML docs enforced, logging registry ([logging.md](logging.md)), performance check, three rounds of independent code review ([reviews/](reviews/)), all findings fixed. |

**Tests:** 321 unit + 281 integration + 42 WPF (Linux-runnable), zero warnings. Gated: 7 need the network (`ALCOVE_NETWORK_TESTS=1`), 1 is the performance test (`ALCOVE_PERF_TESTS=1`), and 6 WPF runtime scenarios run only on Windows.

**Windows test baseline** (2026-10-20, Windows 11, .NET SDK 10.0.302, Debug, `dotnet test Alcove.slnx`): 320 + 1 skipped unit, 278 + 11 skipped integration (7 network, 1 performance, 3 native scenarios that need gcc), 48 WPF (the 6 runtime scenarios ran). Before the fixes below, 2 min 27 s (integration run 138 s) and an assembly cleanup error; after, 1 min 16 s (integration run 69 s). What made Windows slow, measured by timing each nested `dotnet` command and each test:
- **Temp folder cleanup retries (−46 s).** Tests whose cells loaded assemblies from their temp folder called `DisposeBestEffort`, which went through `Dispose`'s retries: 3 s of sleeping per test before giving up, since Windows keeps loaded assemblies locked until the process exits. It now tries once. `HostingFixtures` used the strict `Dispose` on a workspace that development plugin tests load from in place, which was the cleanup error.
- **NuGet's chain build retries (−14 s).** On Windows, NuGet retries a certificate chain ending in an untrusted root 3 times, 1 s apart, so each verification of a self-signed test package took 3 s (also with offline revocation). The integration tests set `NUGET_EXPERIMENTAL_CHAIN_BUILD_RETRY_POLICY=0,0`; see the [Windows plan's findings](windows-plan.md#findings) for what it means for apps.
- **Cell fixtures built three times (−9 s).** `CellFixtures` was a class fixture of three classes; it's a collection fixture now.
- Not the cause: nested `dotnet build`/`pack` (about 3–4 s each, as on Linux) or Defender. Left as is: the three assembly fixtures initialise one after another (about 35 s before the first test); initialising them in parallel would save about 20 s more but needs restructuring how tests receive them.

**Performance** (this Linux machine, Debug): `LoadAsync` with 50 plugins ≈ 35 ms warm / 95 ms cold; one hot `InstallAsync` from a folder feed ≈ 220 ms; `VerifyAsync` (52 packages) ≈ 15 ms.

**Try it:** `samples/run-console-sample.sh`, or on Windows `samples\run-wpf-sample.ps1` ([samples/README.md](../samples/README.md)).

## Waiting on you

The Windows work is planned in [windows-plan.md](windows-plan.md).


1. **Signature verification on Windows:** the signature spike's `Verifier matrix` (the last Phase 0b item).
2. **Publishing:** `0.0.1-alpha` is on nuget.org (2026-10-20). Push a `v<version>` tag to publish the next one (see CONTRIBUTING.md).
3. **Development plugins in the IDEs:** the [IDE checklist](design/development-plugins.md#ide-checklist) (F5 on a class library, breakpoints, Hot Reload) in Visual Studio 2026, Rider and VS Code.
4. **Push Phase 7** so the Windows CI job runs the WPF scenarios (`tests/Alcove.Wpf.Tests/Windows`). They're skipped on Linux, so the add-on hasn't run against a real WPF yet. Optionally, also try `samples\run-wpf-sample.ps1` by hand.

CI (`.github/workflows/ci.yml`) builds and tests on Linux and Windows on every push to `main` and on pull requests; both were green before Phase 7.

## Later (wanted, not v1)

See [open-questions.md](open-questions.md#deferred-wanted-not-v1): `Alcove.Tool`, `Alcove.Testing`, `Alcove.Wpf.Manager`, `Alcove.Templates`, optional plugin dependencies, `IConfiguration` binding, NuGet plugin credential providers, `TrustPolicy.FromNuGetConfig`, strict revocation, refreshing nuget.org's repository certificate list before May 2027, and single-file host support.

## Known limitations

- Single-file and trimmed hosts are unsupported (`UnsupportedHostException` for single-file).
- Plugins run with full trust; the trust policy controls what gets installed, not what installed code can do.
- NuGet's plugin credential providers (e.g. Azure Artifacts) aren't supported.
- A plugin later skipped in a cell (e.g. its dependency faulted) may still have contributed services to the cell's provider.

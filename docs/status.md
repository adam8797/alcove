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
| 8 | Hardening | Public API snapshots, XML docs enforced, logging registry ([logging.md](logging.md)), performance check, three rounds of independent code review ([reviews/](reviews/)), all findings fixed. |

**Tests:** 311 unit + 263 integration + 42 WPF (Linux-runnable), zero warnings. Gated: 7 need the network (`ALCOVE_NETWORK_TESTS=1`), 1 is the performance test (`ALCOVE_PERF_TESTS=1`), and 6 WPF runtime scenarios run only on Windows.

**Performance** (this Linux machine, Debug): `LoadAsync` with 50 plugins ≈ 35 ms warm / 95 ms cold; one hot `InstallAsync` from a folder feed ≈ 220 ms; `VerifyAsync` (52 packages) ≈ 15 ms.

**Try it:** `samples/run-console-sample.sh`, or on Windows `samples\run-wpf-sample.ps1` ([samples/README.md](../samples/README.md)).

## Waiting on you

1. **Signature verification on Windows:** the signature spike's `Verifier matrix` (the last Phase 0b item).
2. **Publishing:** no packages have been published anywhere.
3. **Push Phase 7** so the Windows CI job runs the WPF scenarios (`tests/Alcove.Wpf.Tests/Windows`). They're skipped on Linux, so the add-on hasn't run against a real WPF yet. Optionally, also try `samples\run-wpf-sample.ps1` by hand.

CI (`.github/workflows/ci.yml`) builds and tests on Linux and Windows on every push to `main` and on pull requests; both were green before Phase 7.

## Later (wanted, not v1)

See [open-questions.md](open-questions.md#deferred-wanted-not-v1): `Alcove.Tool`, `Alcove.Testing`, `Alcove.Wpf.Manager`, `Alcove.Templates`, optional plugin dependencies, `IConfiguration` binding, NuGet plugin credential providers, `TrustPolicy.FromNuGetConfig`, strict revocation, refreshing nuget.org's repository certificate list before May 2027, and single-file host support.

## Known limitations

- Single-file and trimmed hosts are unsupported (`UnsupportedHostException` for single-file).
- Plugins run with full trust; the trust policy controls what gets installed, not what installed code can do.
- NuGet's plugin credential providers (e.g. Azure Artifacts) aren't supported.
- A plugin later skipped in a cell (e.g. its dependency faulted) may still have contributed services to the cell's provider.

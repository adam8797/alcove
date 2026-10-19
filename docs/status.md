# Status

*Last updated 2026-10-19.*

## Done

| Phase | What | Notes |
|---|---|---|
| 0 | Spikes | All five ran on Linux. The WPF spike's results are predictions until it's run on Windows (Phase 0b). |
| 1 | Repository foundation | Solution, central package management, Gitea Actions CI (not run yet: nothing is pushed). |
| 2 | `Alcove.Abstractions`, `Alcove.Sdk` | Manifest generation, explicit `AssemblyVersion`, host-owned packages, `ALCOVE001`–`006`. |
| 3 | Offline core | Cells (load order, answer freezing, version checks, natives, satellites), exposed-dependency scanner, WPF metadata analysis, validation, activation, extension catalog, `PluginHost`, console sample. |
| 4 | Feeds and install | Search, `RemoteDependencyWalker` resolution (matches `dotnet restore`), download, `InstallAsync` with the hot-load rule, trust policy and signature verification with the shipped root bundle, localhost test feed. |
| 5 | Lifecycle | Staged update/uninstall/enable/disable/rollback, startup apply and cleanup, in-use records, pending changes, update checks, restore, verify. |
| 6 | DI add-ons | `Alcove.DependencyInjection` and `Alcove.Autofac`, one service provider per cell, shared test suite. |
| 8 | Hardening | Public API snapshots, XML docs enforced, logging registry ([logging.md](logging.md)), performance check, three rounds of independent code review ([reviews/](reviews/)), all findings fixed. |

**Tests:** 310 unit + 258 integration, zero warnings. 8 tests are gated: 7 need the network (`ALCOVE_NETWORK_TESTS=1`) and 1 is the performance test (`ALCOVE_PERF_TESTS=1`).

**Performance** (this Linux machine, Debug): `LoadAsync` with 50 plugins ≈ 35 ms warm / 95 ms cold; one hot `InstallAsync` from a folder feed ≈ 220 ms; `VerifyAsync` (52 packages) ≈ 15 ms.

**Try it:** `samples/run-console-sample.sh`.

## Waiting on you

1. **Windows runs (Phase 0b):**
   - `spike\wpf-across-load-contexts\run-windows.ps1`: should end with `ALL PREDICTIONS MATCHED`; paste back `results\summary.txt`.
   - The signature spike's `Verifier matrix` on Windows.
   - Also worth running `dotnet test Alcove.slnx` on Windows. All development so far was on Linux, so Windows-specific behaviour (file locks during cleanup, sharing violations, native library names) is untested there.
2. **Phase 7 (WPF add-on):** on hold until the Windows run confirms the WPF spike's predictions.
3. **Pushing to Gitea:** everything is committed locally on `main`; nothing has been pushed. CI (`.gitea/workflows/ci.yml`) runs once it's pushed.
4. **Publishing:** no packages have been published anywhere.

## Later (wanted, not v1)

See [open-questions.md](open-questions.md#deferred-wanted-not-v1): `Alcove.Tool`, `Alcove.Testing`, `Alcove.Wpf.Manager`, `Alcove.Templates`, optional plugin dependencies, `IConfiguration` binding, NuGet plugin credential providers, `TrustPolicy.FromNuGetConfig`, strict revocation, refreshing nuget.org's repository certificate list before May 2027, and single-file host support.

## Known limitations

- Single-file and trimmed hosts are unsupported (`UnsupportedHostException` for single-file).
- Plugins run with full trust; the trust policy controls what gets installed, not what installed code can do.
- NuGet's plugin credential providers (e.g. Azure Artifacts) aren't supported.
- A plugin later skipped in a cell (e.g. its dependency faulted) may still have contributed services to the cell's provider.

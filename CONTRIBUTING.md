# Contributing to Alcove

Read [CONTEXT.md](CONTEXT.md) first: use its terms in code, comments and docs. The design is in [docs/architecture.md](docs/architecture.md) and [docs/adr/](docs/adr/). The work order is in [docs/implementation-plan.md](docs/implementation-plan.md).

## Toolchain

- .NET SDK pinned by `global.json` (10.0.203, `rollForward: latestFeature`).
- All projects target `net10.0`, except WPF projects (`net10.0-windows`, with `EnableWindowsTargeting=true` so they build on Linux).
- Central package management: versions live only in `Directory.Packages.props`.
- Build: `dotnet build Alcove.slnx`. Test: `dotnet test Alcove.slnx`. Both must pass with **zero warnings**.

## Code style

- C# latest, nullable enabled, `TreatWarningsAsErrors=true`, implicit usings on, file-scoped namespaces.
- **Namespaces:**
  - `Alcove`: the public API in both `Alcove.Abstractions` and the core (so a plugin needs only `using Alcove;`);
  - `Alcove.Internal.*`: internal types in the core;
  - add-ons use their own namespaces (`Alcove.DependencyInjection`, `Alcove.Autofac`, `Alcove.Wpf`).
- Public API is deliberate. Everything is `internal` unless it's in the docs or needed by an add-on. Use `InternalsVisibleTo` for tests, not `public`.
- XML doc comments on every public type and member. Keep them short, using CONTEXT.md terms.
- `sealed` by default. Prefer records for plain data. No static mutable state (ADR 0012), except the documented process-wide exceptions.
- Async APIs take a `CancellationToken` (last parameter, with a default value; a shorter convenience overload such as `InstallAsync(string, CancellationToken)` may omit the default) and use `ConfigureAwait(false)` in library code, except where plugin code runs, which keeps the caller's context.
- Logging through `ILogger` with `LoggerMessage` source-generated methods and stable event IDs. Never `Console.Write` in library code.
- Errors the app should handle are structured results (validation reports, install results with reasons), not exceptions. Exceptions are for programming errors and unexpected I/O failures.
- JSON with System.Text.Json source generation (`JsonSerializerContext`); no reflection-based serialization.
- No new package dependencies without a reason recorded in the commit message. Core dependencies are limited to `NuGet.Protocol`, `NuGet.DependencyResolver.Core` and `Microsoft.Extensions.Logging.Abstractions` (architecture §1).

## Tests

- xUnit v3. Unit tests are in `tests/Alcove.Tests`, integration tests in `tests/Alcove.IntegrationTests`, and fixture projects in `tests/fixtures/`.
- **`Alcove.Wpf` tests:**
  - `tests/Alcove.Wpf.Tests` targets `net10.0` and runs on every OS. It compiles in the add-on's WPF-free code (pack URIs, keys, view conventions, the forwarder, pre-loading, resource lookup) and tests it with real cells.
  - Its `Windows/` scenarios (`WpfScenarioTests`) pack the fixture plugins in `tests/fixtures/Wpf` once per run. They run `tests/Alcove.Wpf.TestHost`, a `net10.0-windows` WPF host, in a child process per scenario, because WPF's caches and loaded assemblies are process-wide. The host renders off-screen and prints `CHECK name=value` lines for the test to assert on.
  - The WPF runtime scenarios only run on Windows, so in practice in the Windows CI job. On other OSes they're skipped with a reason. The test host builds everywhere.
- **Integration tests use real packages**: fixture projects under `tests/fixtures/` are built and packed into a local folder feed during the test run. No mocked `AssemblyLoadContext`.
- Every test that creates files uses its own temporary directory and deletes it afterwards. Tests never touch the user's real NuGet settings or global packages folder.
- Tests must not need the network unless they're marked `[Trait("Category", "Network")]`. HTTP-feed tests use a throwaway localhost feed.
- Tests that can only run on Windows (WPF) are skipped on other platforms with a clear reason.

## Guard-rail tests

- **Public API snapshots:** `tests/Alcove.Tests/PublicApi/PublicApi.<Assembly>.txt`. An intentional API change fails the test; accept it with `ALCOVE_UPDATE_PUBLIC_API=1 dotnet test tests/Alcove.Tests` and review the diff in the commit.
  - `Alcove.Wpf` (`net10.0-windows`) can't be loaded on Linux, so its snapshot and its event IDs are read with a `MetadataLoadContext`, against the WPF reference assemblies from the targeting pack the build already uses (`tests/Alcove.Tests/TestSupport/WpfAddOnMetadata.cs`). That needs the test-only package `System.Reflection.MetadataLoadContext`, and the add-on must be built first: `tests/Alcove.Tests` has a build-order-only reference to it.
- **Async conventions:** public awaitable methods end in `Async`, and the `CancellationToken` comes last with a default (a shorter overload of a method that has one may omit it).
- **XML docs:** shipping projects treat CS1591, CS1572, CS1573, CS1574 and CS0419 as errors (`src/Directory.Build.props`), so every public member needs correct XML docs.
- **Logging event IDs:** each log class owns one range of 100 (see [docs/logging.md](docs/logging.md)); IDs must be unique.
- **Performance:** `ALCOVE_PERF_TESTS=1 dotnet test tests/Alcove.IntegrationTests --filter Category=Performance --logger "console;verbosity=detailed"`.
- **Network tests** (nuget.org signatures): `ALCOVE_NETWORK_TESTS=1`.

## Git

- Small, focused commits with a message explaining *why*.
- Never commit `bin/`, `obj/`, packed fixtures, downloaded packages or private keys.

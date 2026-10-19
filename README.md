# Alcove

A lightweight, opinion-light .NET library for **installing plugins from NuGet feeds and loading them into any .NET app**: console, desktop (WPF first) or web.

- **NuGet is how plugins are delivered.** Plugins are ordinary NuGet packages: they come from nuget.org, private feeds or local folders, with normal versioning and dependencies.
- **Plugins are isolated in cells.** Each plugin (or a group of plugins you choose) gets its own `AssemblyLoadContext`, so a plugin can bring its own dependency versions without breaking the host.
- **Hot install.** Newly installed plugins load immediately. Updates, uninstalls and enable/disable are staged and applied at next startup.
- **Extension catalog.** The host asks for "every `IToolWindow` from every plugin" and is notified when new ones arrive.
- **No opinion on DI, UI or permissions.** The core uses no DI library. Microsoft DI and Autofac support come as add-on packages. Who may install plugins, where they live, and how they appear in the UI is the app's decision.

## Status

In development. Phases 0–6 and 8 of the [implementation plan](docs/implementation-plan.md) are done: repository foundation, `Alcove.Abstractions`, `Alcove.Sdk`, the offline core (cells, validation, activation, extension catalog, `PluginHost`), feeds and install (search, resolution, `InstallAsync` with hot loading, trust policy and signature verification), lifecycle (staged update, uninstall, enable/disable, rollback, restore, verify), the Microsoft DI and Autofac add-ons, and hardening (public API snapshot, logging review, performance check). Phase 7 (WPF add-on) is on hold until the WPF spike runs on Windows (Phase 0b). Try it with `samples/run-console-sample.sh`. Nothing is published yet.

## Quick look

**App developer** (the host app):

```csharp
var host = PluginHost.Create(new PluginHostOptions
{
    Root = Path.Combine(appDataDir, "plugins"),     // always supplied by the app
    PluginPackageType = "MyAppPlugin",
    HostOwnedPackages = { "MyApp.Contracts", "MyApp.Sdk" },
    Feeds = { new PackageFeed("https://nuget.example.com/v3/index.json") },
});

await host.LoadAsync();                              // validates, then loads

foreach (var window in host.Catalog.GetAll<IToolWindow, ToolWindowMetadata>())
    AddMenuItem(window.Metadata.Title, () => Show(window.Value));

host.Catalog.Observe<IToolWindow>(added => AddMenuItem(added.Plugin.Id, () => Show(added.GetInstance())));   // existing ones, then each hot-installed one

await host.InstallAsync("Contoso.ChartsPlugin");     // loads immediately when possible
```

**Plugin developer:**

```csharp
public sealed class ChartsPlugin : IPlugin
{
    public void Register(IExtensionRegistrar registrar, IPluginContext context)
    {
        registrar.Add<IToolWindow>(() => new ChartWindow(),
            new ToolWindowMetadata { Title = "Charts" });
    }
}
```

```xml
<!-- ChartsPlugin.csproj -->
<PackageReference Include="Alcove.Sdk" Version="1.*" PrivateAssets="all" />
<PackageReference Include="MyApp.Contracts" Version="[2.0,3.0)" />
```

## Packages

| Package | For | Purpose |
|---|---|---|
| `Alcove.Abstractions` | Plugin developers | `IPlugin`, `IPluginContext`, `IExtensionRegistrar`, `ExtensionAttribute`, the plugin manifest model. Small and stable. |
| `Alcove.Sdk` | Plugin developers | MSBuild props/targets: package type, manifest generation, contract handling. |
| `Alcove` | App developers | The core: feeds, resolution, validation, cells, loading, catalog. No UI or DI dependencies. |
| `Alcove.DependencyInjection` | App + plugin developers | Microsoft.Extensions.DependencyInjection integration. |
| `Alcove.Autofac` | App + plugin developers | Autofac integration. |
| `Alcove.Wpf` | App developers | Dispatcher-aware catalog, resource dictionary merging, pack URI and resource-key helpers (on hold). |
| `Alcove.Wpf.Manager` | App developers | Ready-made plugin manager UI (later). |
| `Alcove.Templates` | Plugin developers | `dotnet new` templates (later). |
| `Alcove.Testing` | Both | Test helpers: fake hosts, in-memory feeds (later). |
| `Alcove.Tool` | Operators | `dotnet tool` for managing a plugin root from the command line (later). |

## Documentation

- [docs/status.md](docs/status.md): where things stand and what's next.
- [CONTEXT.md](CONTEXT.md): glossary. Start here.
- [docs/overview.md](docs/overview.md): goals, non-goals, scope.
- [docs/architecture.md](docs/architecture.md): how it works.
- [docs/app-developer-guide.md](docs/app-developer-guide.md): using it in a host app.
- [docs/plugin-developer-guide.md](docs/plugin-developer-guide.md): writing a plugin.
- [docs/adr/](docs/adr/): architecture decision records.
- [docs/open-questions.md](docs/open-questions.md): unresolved items and planned spikes.
- [docs/logging.md](docs/logging.md): logging event IDs.
- [docs/reviews/](docs/reviews/): code reviews.
- [samples/README.md](samples/README.md): the runnable console sample.
- [CONTRIBUTING.md](CONTRIBUTING.md): toolchain, code style, guard-rail tests.

## License

MIT

# Alcove

A lightweight, opinion-light .NET library for **installing plugins from NuGet feeds and loading them into any .NET app**: console, desktop (WPF) or web.

- **NuGet is how plugins are delivered.** Plugins are ordinary NuGet packages, from nuget.org, private feeds or local folders.
- **Plugins are isolated in cells.** Each plugin (or a group you choose) gets its own `AssemblyLoadContext`, so it can bring its own dependency versions.
- **Hot install.** New plugins load immediately; updates, uninstalls and enable/disable apply at next startup.
- **Extension catalog.** The host asks for every `IToolWindow` from every plugin and is notified when new ones arrive.
- **No opinion on DI, UI or permissions.** Microsoft DI, Autofac and WPF support come as add-on packages.

| Package | For |
|---|---|
| `Alcove` | App developers: the core (feeds, resolution, cells, loading, catalog, trust, lifecycle) |
| `Alcove.Abstractions` | Plugin developers: `IPlugin`, `IPluginContext`, `IExtensionRegistrar`, `ExtensionAttribute` |
| `Alcove.Sdk` | Plugin developers: build support (package type, manifest, host-owned references) |
| `Alcove.DependencyInjection` / `Alcove.Autofac` | DI integration |
| `Alcove.Wpf` | WPF integration |

**Status:** early prerelease; the API may change.

- [App developer guide](https://github.com/adam8797/alcove/blob/main/docs/app-developer-guide.md)
- [Plugin developer guide](https://github.com/adam8797/alcove/blob/main/docs/plugin-developer-guide.md)
- [Architecture](https://github.com/adam8797/alcove/blob/main/docs/architecture.md)
- [Source and issues](https://github.com/adam8797/alcove)

MIT licensed.

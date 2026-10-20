# Alcove.Wpf

WPF add-on for [Alcove](https://github.com/adam8797/alcove): plugins with XAML inside cells
(`AssemblyLoadContext`s). WPF finds assemblies by simple name among everything loaded in the process, and its own
fallback loads go to the default context, never to a cell. This package makes plugins' XAML work there.

## Host

```csharp
using Alcove;
using Alcove.Wpf;
using Alcove.Wpf.Threading;   // OnDispatcher

var options = new PluginHostOptions { Root = root, PluginPackageType = "MyAppPlugin", HostOwnedPackages = { "MyApp.Sdk" } };
options.UseWpf(Application.Current, wpf =>
{
    wpf.ForwardDefaultContextLoads = true;   // opt-in, process-wide while the host lives (see below)
    wpf.MapViewModelsToViews = true;         // opt-in: FooViewModel -> FooView DataTemplates
});
await using var host = PluginHost.Create(options);
var catalog = host.Catalog.OnDispatcher(Application.Current.Dispatcher);   // callbacks on the UI thread
catalog.Observe<IToolWindow>(e => windows.Add(e.GetInstance()));
await host.LoadAsync();
```

`UseWpf` works with `Alcove.DependencyInjection` and `Alcove.Autofac` in either order: the core already creates every
extension inside its cell's contextual reflection scope, whichever activator is set.

Set this switch in the host's project file (a message is logged when it's missing):

```xml
<ItemGroup>
  <RuntimeHostConfigurationOption Include="Switch.System.Windows.Baml2006.AppendLocalAssemblyVersionForSourceUri" Value="true" />
</ItemGroup>
```

| Setting | Default | What it does |
|---|---|---|
| `PreloadWpfAssemblies` | on | Loads each cell's WPF-relevant private assemblies when the cell is created (or grows by a hot install), so a control library used only from XAML is found. |
| `MergePluginResources` | on | Merges each plugin assembly's `[assembly: PluginResources("...")]` dictionaries, or `Themes/Plugin.xaml`, into the app's resources (below the app's own) before the plugin registers; removed when the host is disposed. |
| `ForwardDefaultContextLoads` | off | Registers an `AssemblyLoadContext.Default.Resolving` handler for the host's lifetime: a failed default-context load goes to the single cell owning a private copy of that name (version ignored). Covers rolled-forward XAML libraries and external theme assemblies. Several cells with different copies: not resolved, logged. This is process-wide behaviour, the one opt-in exception to "no global state" (ADR 0012). |
| `MapViewModelsToViews` | off | Adds implicit `DataTemplate`s for `FooViewModel` → `FooView` (same namespace, or `ViewModels` → `Views`). |
| `Resources` | app resources | Where plugins' dictionaries are merged. |

Merging and removing run on the dispatcher's thread: inline when the host loads there, otherwise with a blocking
`Dispatcher.Invoke`. Don't block the UI thread synchronously on `LoadAsync`/`InstallAsync`/`DisposeAsync`; await them.

`DispatcherExtensionCatalog` runs `Observe` and `ExtensionsAdded` callbacks at once when they're raised on the UI
thread, and queues them with `BeginInvoke` otherwise (the loading thread never waits for the UI thread).

## App SDK and plugins

`SharedResourceKey<T>`, `PluginPackUri` and `[assembly: PluginResources(...)]` are in `Alcove.Wpf.Resources`,
`ViewTemplates` in `Alcove.Wpf.Views`, and `DispatcherExtensionCatalog` in `Alcove.Wpf.Threading`.

- Shared resource keys as constants in the app's contracts package or app SDK (host-owned):
  `public static readonly SharedResourceKey<Brush> Accent = new("MyApp.Accent");`, used as
  `{DynamicResource {x:Static sdk:AppKeys.Accent}}`, or from code with `TryFindResource(key, out var brush)`.
- `PluginPackUri.Create(assembly, "Themes/Colors.xaml")` gives `/Name;v1.2.3.4;component/Themes/Colors.xaml` (plus the
  public key token when strong-named): always versioned, so it can't reach another cell's copy of the assembly.
- Plugins referencing `Alcove.Wpf` with `Alcove.Sdk` treat it as host-owned automatically (`buildTransitive`).

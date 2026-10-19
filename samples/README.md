# Samples

## Console host

```
samples/run-console-sample.sh
```

Builds `ConsoleHost` (which packs Alcove), packs the sample plugins in `plugins/` with `Alcove.Sdk` into a local
folder feed under `artifacts/sample/`, then starts the host on a fresh plugin root. The host loads (nothing yet),
searches the feed for `ConsoleHostPlugin` packages and installs each one with `PluginHost.InstallAsync` while
running: each plugin appears in the catalog without a restart. Then it lists the commands and runs them. Finally
it checks for updates, disables the faulted plugin and lists the pending changes: staged, they apply at the next
startup. It needs bash; it doesn't change your NuGet settings or packages.

What it shows:

| Plugins | Scenario |
|---|---|
| `Sample.Greetings` (Sample.Text 1.0), `Sample.Weather` (Sample.Text 2.0) | Two cells with conflicting versions of one dependency |
| `Sample.Report` → `Sample.Formatting` | A plugin depending on another across cells: `Sample.Text` is an exposed dependency of `Sample.Formatting`, so `Sample.Report` gets it through Formatting's cell. Installing Report first installs Formatting with it |
| `Sample.Dashboard` → `Sample.Report` → `Sample.Formatting` | A chain of three plugins, loaded dependencies first |
| `Sample.Broken` | Throws in `InitializeAsync`: it's Faulted, everything else loads |

`ConsoleHost` uses only Alcove's public API.

## WPF host

On Windows (PowerShell):

```
samples\run-wpf-sample.ps1            # fresh plugin root
samples\run-wpf-sample.ps1 -KeepRoot  # again: the installed plugins load at startup
```

Builds `WpfHost` (which packs Alcove, `Alcove.Wpf` and the app SDK `WpfHost.Sdk`), packs the plugins in `wpf-plugins/`
with `Alcove.Sdk` into a local folder feed under `artifacts/wpf-sample/`, then starts the host on a plugin root there.
At first start it installs `Sample.Notes` and `Sample.Charts` from the feed; select `Sample.Clock` under "On the feed"
and press **Install (hot)** to install it while the app runs. It doesn't change your NuGet settings or packages.
`samples/build-wpf-sample.sh` builds and packs everything on Linux or macOS too (WPF itself only runs on Windows).

The host calls `options.UseWpf(app, ...)` (with the opt-in forwarder and view-model mapping on) and shows each
`IToolPanel` extension in a tab, through `host.Catalog.OnDispatcher(Dispatcher)`. Its project sets the
`Switch.System.Windows.Baml2006.AppendLocalAssemblyVersionForSourceUri` switch.

| Project | What it shows |
|---|---|
| `WpfHost.Sdk` | The app SDK, host-owned: the `IToolPanel` extension point and `[ToolPanel]` metadata, a shared `PanelHeader` UserControl, styles, and typed keys (`SampleKeys.AccentBrush`, a `SharedResourceKey<Brush>`) that plugins use with `DynamicResource`. "Switch accent colour" changes the key's value; every plugin follows |
| `Sample.Notes` | A view-model (`NotesViewModel`) shown by convention (`Views.NotesView`); its `Themes/Plugin.xaml` is merged into the app's resources when it loads, before its view uses it as a `StaticResource` |
| `Sample.Charts` | Uses the control library `Sample.Wpf.Controls` only from XAML: Alcove.Wpf pre-loads it into the plugin's cell, or WPF wouldn't find it |
| `Sample.Clock` | Hot-installed with the button. Uses the same `Sample.Wpf.Controls` package as `Sample.Charts`, from its own cell: the identical copy is loaded once and shared (ADR 0015) |

"Open the selected panel in a window" creates a new panel instance (panels are transient extensions) in a window.

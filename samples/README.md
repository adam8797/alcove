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

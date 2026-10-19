# Logging

Alcove logs through `Microsoft.Extensions.Logging` (`PluginHostOptions.LoggerFactory`), with source-generated `LoggerMessage` methods and stable event IDs. Each area owns a range of 100 IDs; a unit test (`EventIdTests`) enforces unique IDs and one range per log class, including `Alcove.Wpf`'s (read with a `MetadataLoadContext`, since it can't load on Linux).

Levels: **Error** only for things that went wrong unexpectedly (plugin faults, handlers that throw, invalid configuration); **Warning** for conditions an app developer should look at (validation errors are also returned in `ValidationReport`, failed installs in results); **Information** for lifecycle milestones; **Debug/Trace** for diagnostics.

| Range | Area (class) |
|---|---|
| 1000–1099 | Cells and assembly loading (`CellLog`) |
| 2000–2099 | Host and plugin activation (`HostLog`) |
| 2100–2199 | Extension catalog (`CatalogLog`) |
| 2200–2299 | Validation (`ValidationLog`) |
| 2300–2399 | Cell service providers (`CellServicesLog`) |
| 3000–3099 | Feeds, resolution and install (`FeedLog`) |
| 4000–4099 | Trust and signatures (`TrustLog`) |
| 5000–5099 | Lifecycle: staged changes, cleanup, restore, verify (`LifecycleLog`) |
| 6000–6099 | WPF add-on, package `Alcove.Wpf` (`WpfLog`) |

| ID | Level | Event |
|---|---|---|
| 1000 | Debug | Cell created |
| 1001 | Trace | Load() answer |
| 1002 | Debug | Assembly unknown to cell and host |
| 1003 | Warning | Frozen answer kept |
| 1004 | Error | Requested version higher than the cell's |
| 1005 | Debug | Resolution failed |
| 1006 | Debug | WPF shared assembly reused |
| 1007 | Trace | LoadUnmanagedDll answer |
| 1008 | Warning | Native library collision |
| 2000 | Information | Loading N plugins |
| 2001 | Information | Plugin loaded |
| 2002 | Error | Plugin faulted |
| 2003 | Warning | Plugin not loaded |
| 2004 | Error | PluginFaulted handler threw |
| 2005 | Warning | Plugin dispose threw |
| 2006 | Information | Load summary |
| 2007 | Debug | Faulted again |
| 2008 | Warning | Cancellation callback failed |
| 2009 | Error | Cell observer threw (`CellCreated` / `CellExtended`; the load continues) |
| 2010 | Warning | Cell observer dispose threw |
| 2100 | Debug | Extensions added |
| 2101 | Error | Observe callback threw |
| 2102 | Error | ExtensionsAdded handler threw |
| 2200 | Warning | Validation error |
| 2201 | Warning | Validation warning |
| 2202 | Information | Validation summary |
| 2203 | Debug | WPF inspection failed |
| 2300 | Debug | Cell service provider created |
| 2301 | Error | Cell service provider creation failed |
| 2302 | Warning | Cell provider dispose threw |
| 3000 | Debug | NuGet debug/info |
| 3001 | Information | NuGet minimal |
| 3002 | Warning | NuGet warning |
| 3003 | Warning | NuGet error (reported as a failed result) |
| 3004 | Warning | Feed not allowed by trust policy |
| 3005 | Warning | Feed failed |
| 3010 | Debug | Resolution walk |
| 3011 | Information | Host-shared package becomes cell-private |
| 3012 | Warning | Diamond dependency |
| 3020 | Information | Installing |
| 3021 | Information | Install finished |
| 3022 | Warning | Trust check warning |
| 4000 | Debug | Trust store |
| 4001 | Warning | Trust store problem |
| 4002 | Error | Invalid trust policy |
| 4003 | Warning | Feed rejected |
| 4004 | Debug | Package accepted |
| 4005 | Warning | Package rejected |
| 5000 | Information | Change staged |
| 5001 | Information | Staged changes applied |
| 5002 | Warning | Apply skipped (root lock held) |
| 5003 | Information | Unused package deleted |
| 5004 | Warning | Package delete failed |
| 5005 | Debug | Cleanup skipped |
| 5006 | Information | Restored |
| 5007 | Warning | Restore failed |
| 5008 | Warning | Verification issue |
| 5009 | Warning | Cleanup failed |
| 6000 | Debug | WPF-relevant assemblies pre-loaded into a cell |
| 6001 | Warning | Pre-loading a WPF-relevant assembly failed |
| 6002 | Debug | Default-context load forwarded to a cell |
| 6003 | Warning | Default-context load not forwarded: several cells own different copies |
| 6004 | Warning | Forwarded load failed in the cell |
| 6005 | Debug | Default-context forwarder registered / removed |
| 6006 | Warning | Plugin has resources to merge, but there's no target (no `Application`, no `Resources`) |
| 6007 | Debug | Plugin's resource dictionaries merged |
| 6008 | Warning | Removing a plugin's merged dictionaries failed |
| 6009 | Information | `AppendLocalAssemblyVersionForSourceUri` AppContext switch not set |
| 6010 | Debug | Plugin's view-models mapped to views |

Alcove's own categories are `Alcove.PluginHost` (activation, feeds, install, lifecycle and cell service providers), `Alcove.Cells`, `Alcove.Catalog`, `Alcove.Validation` and `Alcove.Trust`; `Alcove.Wpf` logs under `Alcove.Wpf`. Plugins log under a category equal to their plugin ID.

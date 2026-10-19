# 0002. Plugins are NuGet packages identified by PackageType + manifest

**Status:** Accepted

## Context
We need delivery, versioning, dependency description and discovery. NuGet already provides all of these, and plugin developers already know how to produce packages. NuGet has no place for plugin-specific settings (cell, isolation, private dependencies, entry point).

## Decision
- A plugin is a NuGet package with a host-specific custom `PackageType` (e.g. `MyAppPlugin`), used for search filtering and identification.
- The package also contains a manifest, `<PackageId>.plugin.json`, holding only what the `.nuspec` can't express.
- Display metadata comes only from the `.nuspec`.
- Compatibility is expressed by the plugin's NuGet version range on the host's contracts package.
- `Alcove.Sdk` generates the package type and manifest. Plugin developers don't write them by hand.

## Consequences
- Any NuGet v3 feed or local folder works. Feed search can filter by package type.
- No custom compatibility format. NuGet version ranges do the job, so contracts packages must follow semantic versioning strictly.
- The manifest needs a schema version and must stay backward compatible.

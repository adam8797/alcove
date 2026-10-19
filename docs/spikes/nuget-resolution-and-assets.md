# Spike: NuGet resolution and asset selection

## Goal
Prove that the NuGet client libraries can do what install, resolution and loading need, and find the APIs to use.

## Questions
1. **Host inventory.** From a running .NET 10 app, read its `deps.json` (`AppContext.GetData("APP_CONTEXT_DEPS_FILES")` or the file next to the entry assembly) with System.Text.Json and produce: package ID → version, and assembly → package. How do shared framework assemblies show up (they're not in `deps.json` as packages)? How do we tell which shared frameworks are loaded (`Microsoft.NETCore.App`, `Microsoft.AspNetCore.App`, `Microsoft.WindowsDesktop.App`)? What about framework-provided packages such as `System.Text.Json` that also exist on NuGet?
2. **Resolution with host constraints.** Using `NuGet.Protocol` + `NuGet.Resolver` (or `NuGet.DependencyResolver` / whatever is the right layer), resolve a plugin package's dependency graph from a feed, treating host packages as already present. For each dependency, classify: host provides a satisfying version / host has a conflicting version / host doesn't have it. Is `PackageResolver` (the packages.config-era resolver) or the PackageReference-style "lowest applicable version" walker the right model? Which one does `dotnet tool install` use?
3. **Multi-plugin resolution per compartment.** Two plugins in one compartment with overlapping dependencies (a diamond): resolve together to one version per package. Detect and report range conflicts.
4. **Asset selection.** Given an extracted package and the host's framework (`net10.0`, `net10.0-windows`) and runtime identifier, select `lib/` assemblies, `runtimes/<rid>/lib`, `runtimes/<rid>/native`, and satellite resource assemblies, using NuGet's own rules (`NuGet.Packaging` `ContentModel` / `ManagedCodeConventions`, `FrameworkReducer`). Compare against what `dotnet build` picks for the same package (e.g. via the `deps.json` of a test app referencing it).
5. **Download and extraction.** Download from nuget.org and from a local folder feed, extract into `packages/<id>/<version>/`, and get the SHA-512 the way NuGet records it (`.nupkg.metadata` `contentHash`).
6. **PackageType filtering.** Can search on nuget.org filter by package type through `NuGet.Protocol` (`PackageSearchResource` with search filter / `packageType=`)? Does a local folder feed support it, or do we filter client-side?
7. **Size.** How large is the dependency footprint of the NuGet packages we'd reference (NuGet.Protocol, NuGet.Resolver, NuGet.Packaging, ...)?

## Experiments
A console app that:
- dumps its own host inventory;
- resolves a real package with a non-trivial graph from nuget.org (e.g. `Serilog.Sinks.File`, `Polly`, `Microsoft.Extensions.Http`) against the host inventory, printing the classification;
- resolves two local fixture plugins with a diamond dependency;
- selects assets for a package with native/RID-specific assets (e.g. `Microsoft.Data.Sqlite`'s `SQLitePCLRaw.lib.e_sqlite3`) for `linux-x64` and `win-x64`;
- downloads and extracts with hash.

## Success criteria
For each question: the API to use (with a working code snippet in the report), caveats, and anything that contradicts the architecture doc. Recommendation of which NuGet packages the core depends on.

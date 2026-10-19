# 0016. Resolve with NuGet's RemoteDependencyWalker and host pins

**Status:** Accepted (backed by spike [nuget-resolution-and-assets](../../spike/nuget-resolution-and-assets/REPORT.md))

## Context
We need NuGet resolution that knows about the host's packages, reports conflicts clearly, and gives the same answers as `dotnet restore`.

## Decision
- Use `RemoteDependencyWalker` from `NuGet.DependencyResolver.Core` (PackageReference semantics), with a small `IRemoteDependencyProvider` over `FindPackageByIdResource`.
- Resolve per cell from a synthetic root that depends on all the cell's plugins and **pins every host package** at the host's version.
- If the walker reports a downgrade: for a host-owned package, the plugin is incompatible; for a host-shared one, unpin it, walk again, and the package becomes cell-private.
- Range conflicts come from `Analyze().VersionConflicts` and are reported as validation errors naming the packages and ranges.
- Don't use `NuGet.Resolver`'s `PackageResolver` (slow, uninformative errors) or `NuGet.Commands` (+1 MB, not needed).
- Select assets with `ManagedCodeConventions` and an embedded RID graph. Take the host framework from entry-assembly attributes.

## Consequences
- Matches `dotnet restore` (4/4 cases tried) and `dotnet build`'s asset selection (all cases tried). Both serve as test oracles.
- Core NuGet footprint: about 3.4 MB in 11 DLLs.

## Amendments
- *2026-10-19 (as built):* range conflicts and downgrades found at install or update are reported as `InstallStatus.Failed` / `ChangeStatus.Failed` reasons (`Incompatible` for host-owned downgrades), not as validation errors. Validation at load only warns (`CellVersionConflict`).

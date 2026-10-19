using Alcove.Internal.Cells;
using NuGet.Versioning;

namespace Alcove.Internal.Classification;

/// <summary>The class of an assembly a plugin might load (architecture §4). It decides which load context provides it.</summary>
internal enum AssemblyClass
{
    /// <summary>Always from the host's (default) context; a version conflict makes the plugin incompatible.</summary>
    HostOwned,

    /// <summary>From the cell of a plugin in the transitive plugin-dependency closure (its own assemblies and its exposed dependencies).</summary>
    PluginProvided,

    /// <summary>Loaded inside the cell from the plugin root.</summary>
    CellPrivate,

    /// <summary>The host's copy, whose version satisfies the plugin.</summary>
    HostShared,
}

/// <summary>One classified assembly of a cell.</summary>
/// <param name="Name">The assembly's simple name.</param>
/// <param name="Class">Its class.</param>
/// <param name="PackageId">The package it comes from (for plugin-provided: the provider's package or exposed dependency's package, if known).</param>
/// <param name="PackageVersion">The package version, if known.</param>
/// <param name="Path">The file, for cell-private assemblies.</param>
/// <param name="ProviderCell">The providing cell, for plugin-provided assemblies.</param>
/// <param name="ProviderPlugin">The providing plugin, for plugin-provided assemblies.</param>
internal sealed record ClassifiedAssembly(
    string Name,
    AssemblyClass Class,
    string PackageId,
    NuGetVersion? PackageVersion,
    string? Path = null,
    string? ProviderCell = null,
    string? ProviderPlugin = null)
{
    /// <summary>Whether the state file marks the assembly as WPF-relevant.</summary>
    public bool WpfRelevant { get; init; }
}

/// <summary>What the classifier found wrong. Validation (not the classifier) decides how to report it.</summary>
internal enum ClassificationIssueKind
{
    /// <summary>A plugin needs a higher version of a host-owned package than the host has: the plugin is incompatible.</summary>
    HostOwnedConflict,

    /// <summary>A plugin needs a host-owned package the host doesn't carry: the plugin is incompatible.</summary>
    HostOwnedMissing,

    /// <summary>The state says host-shared, but the host's version is now lower than the resolved one; the cell uses its private copy if there is one.</summary>
    HostSharedDowngraded,

    /// <summary>A cell-private package isn't in the plugin root.</summary>
    PackageMissing,

    /// <summary>Two plugins in one cell resolved different versions of one package; the highest is used.</summary>
    CellVersionConflict,

    /// <summary>A consumer needs a higher version of a provider's exposed dependency than the provider's cell has.</summary>
    ExposedDependencyConflict,

    /// <summary>A plugin depends on a plugin that isn't installed or enabled.</summary>
    PluginDependencyMissing,
}

/// <summary>A problem found while classifying.</summary>
internal sealed record ClassificationIssue(ClassificationIssueKind Kind, string Cell, string PluginId, string PackageId, string Message);

/// <summary>The classification of one cell: its plugins, every assembly's class, and the plan its <see cref="Cell"/> uses.</summary>
internal sealed record CellClassification(
    string Cell,
    IReadOnlyList<string> Plugins,
    IReadOnlyList<ClassifiedAssembly> Assemblies,
    CellPlan Plan,
    IReadOnlyList<ClassificationIssue> Issues);

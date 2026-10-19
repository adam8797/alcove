using NuGet.Versioning;

namespace Alcove;

/// <summary>
/// A plugin package found by <see cref="PluginHost.SearchAsync(string, CancellationToken)"/>: its latest version and
/// display metadata (from the feed or the <c>.nuspec</c>), and whether it suits this host. Incompatible results are
/// flagged, not hidden.
/// </summary>
public sealed record PluginSearchResult
{
    internal PluginSearchResult(string id, NuGetVersion version)
    {
        Id = id;
        Version = version;
    }

    /// <summary>The package ID.</summary>
    public string Id { get; }

    /// <summary>The latest version found (stable unless prerelease versions are included).</summary>
    public NuGetVersion Version { get; }

    /// <summary>The title, or the ID if the package has none.</summary>
    public string Title { get; init; } = "";

    /// <summary>The description.</summary>
    public string? Description { get; init; }

    /// <summary>The authors, as the package lists them.</summary>
    public string? Authors { get; init; }

    /// <summary>The icon URL, if any.</summary>
    public Uri? IconUrl { get; init; }

    /// <summary>The project URL, if any.</summary>
    public Uri? ProjectUrl { get; init; }

    /// <summary>The feed it was found on (the first one, if several have it).</summary>
    public string Source { get; init; } = "";

    /// <summary>
    /// Whether this host can run the latest version, judged cheaply from its declared dependencies: the ranges on
    /// host-owned packages (e.g. the contracts) against the host's versions, and the target framework.
    /// </summary>
    public bool IsCompatible { get; init; } = true;

    /// <summary>Why it isn't compatible, or <see langword="null"/>.</summary>
    public string? IncompatibilityReason { get; init; }
}

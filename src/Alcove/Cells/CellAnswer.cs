using NuGet.Versioning;

namespace Alcove.Cells;

/// <summary>Where an answer of a cell's <c>Load</c> comes from.</summary>
internal enum AnswerSource
{
    /// <summary><c>null</c>: the host's (default) context provides it.</summary>
    Host,

    /// <summary>Loaded inside the cell from a file in the plugin root.</summary>
    Private,

    /// <summary>Delegated to another cell (a plugin dependency or one of its exposed dependencies).</summary>
    Provider,

    /// <summary>Resolution failed. Failures aren't cached by the runtime, so they never block a later answer.</summary>
    Failed,
}

/// <summary>Why a cell gave an answer (diagnostics; not part of answer identity).</summary>
internal enum AnswerReason
{
    /// <summary>Host-owned (resolution step 1).</summary>
    HostOwned,

    /// <summary>A plugin in the transitive plugin-dependency closure, or one of its exposed dependencies (step 2).</summary>
    PluginProvided,

    /// <summary>Cell-private (step 3).</summary>
    CellPrivate,

    /// <summary>An identical WPF-relevant private assembly already loaded by another cell (step 3a).</summary>
    SharedWpfRelevant,

    /// <summary>Host-shared (step 4).</summary>
    HostShared,

    /// <summary>Not known to the cell or the host (step 5).</summary>
    Unknown,

    /// <summary>Resolution failed.</summary>
    Failed,
}

/// <summary>
/// One answer of a cell for an assembly name or a native library: where it comes from and, for private
/// answers, which exact file of which package version.
/// </summary>
internal sealed record CellAnswer(AnswerSource Source, AnswerReason Reason)
{
    /// <summary>The file, for private answers (assemblies and native libraries).</summary>
    public string? Path { get; init; }

    /// <summary>The package the file belongs to, for private answers.</summary>
    public string? PackageId { get; init; }

    /// <summary>The package version, for private answers.</summary>
    public NuGetVersion? PackageVersion { get; init; }

    /// <summary>The providing cell, for provider answers.</summary>
    public string? ProviderCell { get; init; }

    /// <summary>Why it failed, for failed answers.</summary>
    public string? Error { get; init; }

    /// <summary>"Use the host's copy".</summary>
    public static CellAnswer Host(AnswerReason reason) => new(AnswerSource.Host, reason);

    /// <summary>A failure.</summary>
    public static CellAnswer Failed(string error) => new(AnswerSource.Failed, AnswerReason.Failed) { Error = error };

    /// <summary>
    /// Whether two answers are the same for the hot-load rule: same source; for private answers the same
    /// package version and file; for provider answers the same cell.
    /// </summary>
    public bool SameAs(CellAnswer other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Source == other.Source && Source switch
        {
            AnswerSource.Private => string.Equals(Path, other.Path, StringComparison.Ordinal)
                && string.Equals(PackageId, other.PackageId, StringComparison.OrdinalIgnoreCase)
                && Equals(PackageVersion, other.PackageVersion),
            AnswerSource.Provider => string.Equals(ProviderCell, other.ProviderCell, StringComparison.Ordinal),
            _ => true,
        };
    }

    /// <inheritdoc />
    public override string ToString() => Source switch
    {
        AnswerSource.Private => $"private {PackageId} {PackageVersion} ({Path})",
        AnswerSource.Provider => $"cell '{ProviderCell}'",
        AnswerSource.Failed => $"failed: {Error}",
        _ => $"host ({Reason})",
    };
}

/// <summary>
/// A snapshot of a cell's committed answers: the plan merged with every answer actually given (a given
/// success always wins; a failure only shows where nothing is planned).
/// </summary>
/// <param name="Assemblies">By assembly simple name; satellites as <c>name/culture</c>.</param>
/// <param name="NativeLibraries">
/// By native file name. An answer actually given is also listed under the requested name and every file name it may
/// refer to (<see cref="Packaging.NativeLibraryNames.Candidates"/>), where nothing else is listed.
/// </param>
internal sealed record CommittedAnswers(
    IReadOnlyDictionary<string, CellAnswer> Assemblies,
    IReadOnlyDictionary<string, CellAnswer> NativeLibraries);

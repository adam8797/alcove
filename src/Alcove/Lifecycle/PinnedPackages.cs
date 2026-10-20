using Alcove.Storage;
using NuGet.Versioning;

namespace Alcove.Lifecycle;

/// <summary>A package the state file pins: exact version, <c>contentHash</c> and the file records of its extracted folder.</summary>
/// <param name="Id">The package ID.</param>
/// <param name="Version">The exact version.</param>
/// <param name="ContentHash">The pinned <c>contentHash</c>, if recorded.</param>
/// <param name="Files">The recorded files of its extracted folder.</param>
internal sealed record PinnedPackage(string Id, NuGetVersion Version, string? ContentHash, IReadOnlyList<FileRecord> Files)
{
    /// <summary>
    /// Every package in the plugin root the state file refers to (each once): plugin packages of active, staged and
    /// previous versions, and their cell-private dependencies.
    /// </summary>
    public static IReadOnlyList<PinnedPackage> All(StateDocument state)
    {
        var found = new Dictionary<(string, NuGetVersion), PinnedPackage>();
        foreach (var record in state.Plugins.SelectMany(StagedChanges.VersionRecords))
        {
            found.TryAdd((record.Id.ToLowerInvariant(), record.Version), new PinnedPackage(record.Id, record.Version, record.ContentHash, record.Files));
            foreach (var dependency in record.Dependencies.Where(d => d.Classification == DependencyClass.CellPrivate))
            {
                found.TryAdd((dependency.Id.ToLowerInvariant(), dependency.Version), new PinnedPackage(dependency.Id, dependency.Version, dependency.ContentHash, dependency.Files));
            }
        }

        return found.Values.ToList();
    }

    /// <summary>The state with new file records for re-extracted packages, wherever an entry refers to them.</summary>
    public static StateDocument WithFiles(StateDocument state, IReadOnlyDictionary<(string Id, NuGetVersion Version), IReadOnlyList<FileRecord>> files)
    {
        return state with { Plugins = state.Plugins.Select(Refresh).ToList() };

        PluginState Refresh(PluginState plugin) => plugin with
        {
            Files = files.GetValueOrDefault((plugin.Id.ToLowerInvariant(), plugin.Version)) ?? plugin.Files,
            Dependencies = plugin.Dependencies
                .Select(d => d.Classification == DependencyClass.CellPrivate && files.TryGetValue((d.Id.ToLowerInvariant(), d.Version), out var records) ? d with { Files = records } : d)
                .ToList(),
            PreviousVersions = plugin.PreviousVersions.Select(Refresh).ToList(),
            StagedChange = plugin.StagedChange is { Target: { } target } change ? change with { Target = Refresh(target) } : plugin.StagedChange,
        };
    }
}

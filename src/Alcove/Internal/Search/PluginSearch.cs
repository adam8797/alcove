using Alcove.Internal.Feeds;
using Alcove.Internal.Hosting;
using Alcove.Internal.Trust;
using Microsoft.Extensions.Logging;
using NuGet.Packaging;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace Alcove.Internal.Search;

/// <summary>
/// Searches the feeds for plugin packages (filtered by the app's package type on the server, or by a folder feed),
/// keeps the latest version of each ID (folder feeds return each version separately) and flags each result's
/// compatibility with the host. A feed that fails is logged and skipped.
/// </summary>
internal sealed class PluginSearch(HostInventory host, PluginHostOptions options, FeedConfiguration feeds, IPackageVerifier verifier, ILogger logger)
{
    /// <summary>Folder feeds are searched completely and paged after grouping by ID.</summary>
    private const int FolderFeedLimit = 10_000;

    public async Task<IReadOnlyList<PluginSearchResult>> SearchAsync(string query, SearchOptions search, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(search);
        var prerelease = search.IncludePrerelease ?? options.AllowPrerelease;
        using var session = new FeedSession(feeds, verifier, logger, cancellationToken);
        var found = new Dictionary<string, (IPackageSearchMetadata Metadata, SourceRepository Source)>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var repository in session.Repositories)
        {
            IReadOnlyList<IPackageSearchMetadata> hits;
            try
            {
                var resource = await repository.GetResourceAsync<PackageSearchResource>(cancellationToken).ConfigureAwait(false);
                if (resource is null)
                {
                    continue;
                }

                var filter = new SearchFilter(prerelease) { PackageType = options.PluginPackageType };
                var isFolder = !repository.PackageSource.IsHttp;
                hits = (await resource.SearchAsync(query ?? "", filter, isFolder ? 0 : search.Skip, isFolder ? FolderFeedLimit : search.Take, session.NuGetLogger, cancellationToken).ConfigureAwait(false)).ToList();
                if (isFolder)
                {
                    hits = Latest(hits, prerelease).Skip(search.Skip).Take(search.Take).ToList();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                FeedLog.FeedFailed(logger, repository.PackageSource.Source, ex.Message);
                continue;
            }

            foreach (var hit in Latest(hits, prerelease))
            {
                var id = hit.Identity.Id;
                if (!found.TryGetValue(id, out var existing))
                {
                    order.Add(id);
                    found[id] = (hit, repository);
                }
                else if (hit.Identity.Version > existing.Metadata.Identity.Version)
                {
                    found[id] = (hit, repository);
                }
            }
        }

        var results = new List<PluginSearchResult>();
        foreach (var id in order)
        {
            var (metadata, source) = found[id];
            var reason = await CheckAsync(metadata, source, session, cancellationToken).ConfigureAwait(false);
            results.Add(new PluginSearchResult(metadata.Identity.Id, metadata.Identity.Version)
            {
                Title = string.IsNullOrWhiteSpace(metadata.Title) ? metadata.Identity.Id : metadata.Title,
                Description = metadata.Description,
                Authors = metadata.Authors,
                IconUrl = metadata.IconUrl,
                ProjectUrl = metadata.ProjectUrl,
                Source = source.PackageSource.Source,
                IsCompatible = reason is null,
                IncompatibilityReason = reason,
            });
        }

        return results;
    }

    /// <summary>Every version of a package on the allowed feeds (stable only unless prerelease is allowed), ascending.</summary>
    public async Task<IReadOnlyList<NuGetVersion>> GetVersionsAsync(string packageId, CancellationToken cancellationToken)
    {
        using var session = new FeedSession(feeds, verifier, logger, cancellationToken);
        var versions = new SortedSet<NuGetVersion>();
        foreach (var repository in session.Repositories)
        {
            try
            {
                versions.UnionWith(await session.GetVersionsAsync(repository, packageId).ConfigureAwait(false));
            }
            catch (FeedException ex)
            {
                FeedLog.FeedFailed(logger, repository.PackageSource.Source, ex.Message);
            }
        }

        return versions.Where(v => options.AllowPrerelease || !v.IsPrerelease).ToList();
    }

    private static IEnumerable<IPackageSearchMetadata> Latest(IEnumerable<IPackageSearchMetadata> hits, bool prerelease) =>
        hits.Where(h => prerelease || !h.Identity.Version.IsPrerelease)
            .GroupBy(h => h.Identity.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.MaxBy(h => h.Identity.Version)!);

    private async Task<string?> CheckAsync(IPackageSearchMetadata metadata, SourceRepository source, FeedSession session, CancellationToken cancellationToken)
    {
        var groups = metadata.DependencySets?.ToList() ?? [];
        if (groups.Count == 0)
        {
            // V3 search results carry no dependencies; read them from the package's .nuspec.
            try
            {
                var resource = await source.GetResourceAsync<FindPackageByIdResource>(cancellationToken).ConfigureAwait(false);
                var info = resource is null
                    ? null
                    : await resource.GetDependencyInfoAsync(metadata.Identity.Id, metadata.Identity.Version, session.Cache, session.NuGetLogger, cancellationToken).ConfigureAwait(false);
                groups = info?.DependencyGroups.ToList() ?? [];
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                FeedLog.FeedFailed(logger, source.PackageSource.Source, ex.Message);
                return $"Its dependencies couldn't be read from the feed: {ex.Message}";
            }
        }

        return HostCompatibility.Check(host, groups);
    }
}

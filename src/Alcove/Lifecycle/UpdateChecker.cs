using Alcove.Feeds;
using Alcove.Hosting;
using Alcove.Install;
using Alcove.Resolution;
using Alcove.Storage;
using Alcove.Trust;
using Microsoft.Extensions.Logging;
using NuGet.Packaging;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace Alcove.Lifecycle;

/// <summary>
/// <c>CheckForUpdatesAsync</c>: for each installed plugin, the newest version on the feeds that this host can run
/// (the cheap check of search: declared host-owned ranges) and that the version ranges of the other plugins accept;
/// newer versions that can't be used are listed with the reason. Reads only <c>.nuspec</c>s; downloads nothing.
/// A feed that fails is logged and skipped.
/// </summary>
internal sealed class UpdateChecker(HostInventory host, PluginHostOptions options, FeedConfiguration feeds, IPackageVerifier verifier, ILogger logger)
{
    public async Task<IReadOnlyList<PluginUpdate>> CheckAsync(StateDocument state, CancellationToken cancellationToken)
    {
        var next = StagedChanges.Effective(state);
        using var session = new FeedSession(feeds, verifier, logger, cancellationToken);
        var results = new List<PluginUpdate>();
        foreach (var plugin in next.Plugins)
        {
            var versions = new SortedDictionary<NuGetVersion, SourceRepository>();
            foreach (var repository in session.Repositories)
            {
                try
                {
                    foreach (var version in await session.GetVersionsAsync(repository, plugin.Id).ConfigureAwait(false))
                    {
                        versions.TryAdd(version, repository);
                    }
                }
                catch (FeedException ex)
                {
                    FeedLog.FeedFailed(logger, repository.PackageSource.Source, ex.Message);
                }
            }

            var prerelease = options.AllowPrerelease || plugin.Version.IsPrerelease;
            var constraints = InstallPlanner.DependentRanges(next, plugin.Id);
            var incompatible = new List<IncompatibleVersion>();
            NuGetVersion? available = null;
            foreach (var (version, source) in versions.Reverse().Where(v => v.Key > plugin.Version && (prerelease || !v.Key.IsPrerelease)))
            {
                var reason = Constraints(plugin.Id, version, constraints)
                    ?? await CheckDependenciesAsync(next, plugin.Id, version, source, session, cancellationToken).ConfigureAwait(false);
                if (reason is null)
                {
                    available = version;
                    break;
                }

                incompatible.Add(new IncompatibleVersion(version, reason));
            }

            results.Add(new PluginUpdate(plugin.Id, plugin.Version, available, incompatible));
        }

        return results;
    }

    private static string? Constraints(string pluginId, NuGetVersion version, IReadOnlyList<(string PluginId, VersionRange Range)> constraints)
    {
        var blocking = constraints.Where(c => !c.Range.Satisfies(version)).ToList();
        return blocking.Count == 0
            ? null
            : string.Join(" ", blocking.Select(c => $"{c.PluginId} needs {pluginId} {c.Range.PrettyPrint()}."));
    }

    /// <summary>The version's declared dependencies against the host (host-owned ranges) and the installed plugins.</summary>
    private async Task<string?> CheckDependenciesAsync(StateDocument state, string pluginId, NuGetVersion version, SourceRepository source, FeedSession session, CancellationToken cancellationToken)
    {
        IReadOnlyList<PackageDependencyGroup> groups;
        try
        {
            var resource = await source.GetResourceAsync<FindPackageByIdResource>(cancellationToken).ConfigureAwait(false);
            var info = resource is null
                ? null
                : await resource.GetDependencyInfoAsync(pluginId, version, session.Cache, session.NuGetLogger, cancellationToken).ConfigureAwait(false);
            groups = info?.DependencyGroups.ToList() ?? [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            FeedLog.FeedFailed(logger, source.PackageSource.Source, ex.Message);
            return $"Its dependencies couldn't be read from the feed: {ex.Message}";
        }

        if (HostCompatibility.Check(host, groups) is { } incompatible)
        {
            return incompatible;
        }

        var group = NuGet.Frameworks.NuGetFrameworkUtility.GetNearest(groups, host.Framework, g => g.TargetFramework);
        var reasons = (group?.Packages ?? [])
            .Select(d => (Dependency: d, Installed: state.Find(d.Id)))
            .Where(d => d.Installed is not null && !string.Equals(d.Dependency.Id, pluginId, StringComparison.OrdinalIgnoreCase) && !d.Dependency.VersionRange.Satisfies(d.Installed.Version))
            .Select(d => $"It needs the plugin {d.Dependency.Id} {d.Dependency.VersionRange.PrettyPrint()}, but {d.Installed!.Version} is installed.")
            .ToList();
        return reasons.Count == 0 ? null : string.Join(" ", reasons);
    }
}

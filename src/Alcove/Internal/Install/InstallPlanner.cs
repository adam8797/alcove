using Alcove.Internal.Feeds;
using Alcove.Internal.Hosting;
using Alcove.Internal.IO;
using Alcove.Internal.Loading;
using Alcove.Internal.Resolution;
using Alcove.Internal.Search;
using Alcove.Internal.State;
using Microsoft.Extensions.Logging;
using NuGet.DependencyResolver;
using NuGet.Packaging.Core;
using NuGet.Versioning;

namespace Alcove.Internal.Install;

/// <summary>A plugin whose state file entry the install writes: a new one, or one already in an affected cell whose dependency set changed.</summary>
/// <param name="Identity">The plugin package.</param>
/// <param name="Existing">Its current state entry, if it was installed before.</param>
/// <param name="Cell">The cell it's assigned to.</param>
/// <param name="Dependencies">Its new resolved dependency set (cell-private packages without their records yet).</param>
internal sealed record PluginToRecord(PackageIdentity Identity, PluginState? Existing, string Cell, IReadOnlyList<DependencyRecord> Dependencies)
{
    public bool IsNew => Existing is null;
}

/// <summary>What an install will do, or why it can't.</summary>
internal sealed class InstallPlan
{
    /// <summary><see cref="InstallStatus.Failed"/> or <see cref="InstallStatus.Incompatible"/> when nothing can be installed.</summary>
    public InstallStatus? Failure { get; init; }

    /// <summary>Why it failed.</summary>
    public IReadOnlyList<string> Reasons { get; init; } = [];

    /// <summary>The version chosen for the requested plugin.</summary>
    public NuGetVersion? Version { get; init; }

    /// <summary>The plugins being installed: the requested one and new plugin dependencies, dependencies first.</summary>
    public IReadOnlyList<PackageIdentity> NewPlugins { get; init; } = [];

    /// <summary>Every state file entry to write (new plugins and rewritten ones).</summary>
    public IReadOnlyList<PluginToRecord> Plugins { get; init; } = [];

    /// <summary>Packages to verify and extract (not in the plugin root yet): libraries first, then plugins, dependencies first.</summary>
    public IReadOnlyList<FeedPackage> Downloads { get; init; } = [];

    /// <summary>The cells the install resolved again.</summary>
    public IReadOnlyList<string> Cells { get; init; } = [];

    /// <summary>For an update: why nothing changes (e.g. the plugin is already at the newest version).</summary>
    public string? NoChange { get; init; }

    public static InstallPlan Fail(InstallStatus status, IEnumerable<string> reasons, NuGetVersion? version = null) =>
        new() { Failure = status, Reasons = reasons.ToList(), Version = version };
}

/// <summary>
/// Plans an install (architecture §6): picks the version, finds the new plugin dependencies (packages with a plugin
/// manifest are installed as plugins), assigns cells, resolves every affected cell jointly with
/// <see cref="CellResolver"/> (its existing plugins and the new ones; host packages and plugins of other cells
/// pinned), and derives each plugin's resolved dependency set. Downloads only what resolution needs to read.
/// </summary>
internal sealed class InstallPlanner(HostInventory host, PackageLayout layout, PluginHostOptions options, FeedSession session, ILogger logger)
{
    private readonly InstalledDependencyProvider _installed = new(layout, (id, range) => host.IsFrameworkProvided(id, range.MinVersion));

    public async Task<InstallPlan> PlanAsync(StateDocument state, string packageId, VersionRange? range, bool includePrerelease, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);

        // 1. The version: the highest that matches (stable unless prerelease is allowed or the range names one).
        var available = await session.GetVersionsAsync(packageId).ConfigureAwait(false);
        if (available.Count == 0)
        {
            return InstallPlan.Fail(InstallStatus.Failed, [$"{packageId} wasn't found on {FeedList()}."]);
        }

        var prerelease = includePrerelease || range?.MinVersion?.IsPrerelease == true || range?.MaxVersion?.IsPrerelease == true;
        var match = available.LastOrDefault(v => (range?.Satisfies(v.Version) ?? true) && (prerelease || !v.Version.IsPrerelease));
        if (match.Version is null)
        {
            var listed = string.Join(", ", available.TakeLast(5).Select(v => v.Version.ToNormalizedString()));
            return InstallPlan.Fail(InstallStatus.Failed,
                [$"No {(prerelease ? "" : "stable ")}version of {packageId}{(range is null ? "" : " " + range.PrettyPrint())} is available (found: {listed})."]);
        }

        var target = await session.FetchAsync(match.Source, new PackageIdentity(packageId, match.Version)).ConfigureAwait(false);
        if (target is null)
        {
            return InstallPlan.Fail(InstallStatus.Failed, [$"The feed '{match.Source.PackageSource.Source}' lists {packageId} {match.Version} but doesn't serve it."], match.Version);
        }

        if (CheckPluginPackage(target.Metadata, requested: true) is { } notAPlugin)
        {
            return InstallPlan.Fail(InstallStatus.Failed, [notAPlugin], match.Version);
        }

        return await PlanCellsAsync(state, target, replacing: null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Plans an update of an installed plugin (Phase 5): the highest version that <paramref name="range"/> accepts (by
    /// default, any version newer than the current one) and that the host can run, whose version the ranges other
    /// enabled plugins declare on this one accept. Then its cell, and the cells of the plugins depending on it, are
    /// resolved again as for an install. <paramref name="state"/> is the state as the next startup will see it.
    /// </summary>
    public async Task<InstallPlan> PlanUpdateAsync(StateDocument state, PluginState current, VersionRange? range, bool includePrerelease, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(current);
        var packageId = current.Id;
        var available = await session.GetVersionsAsync(packageId).ConfigureAwait(false);
        if (available.Count == 0)
        {
            return InstallPlan.Fail(InstallStatus.Failed, [$"{packageId} wasn't found on {FeedList()}."]);
        }

        var prerelease = includePrerelease || current.Version.IsPrerelease
            || range?.MinVersion?.IsPrerelease == true || range?.MaxVersion?.IsPrerelease == true;
        var candidates = available
            .Where(v => (range?.Satisfies(v.Version) ?? v.Version > current.Version) && (prerelease || !v.Version.IsPrerelease))
            .Reverse()
            .ToList();
        if (candidates.Count == 0)
        {
            return range is null
                ? new InstallPlan { NoChange = $"{packageId} {current.Version} is the newest {(prerelease ? "" : "stable ")}version on the feeds.", Version = current.Version }
                : InstallPlan.Fail(InstallStatus.Failed, [$"No {(prerelease ? "" : "stable ")}version of {packageId} {range.PrettyPrint()} is available (found: {string.Join(", ", available.TakeLast(5).Select(v => v.Version.ToNormalizedString()))})."]);
        }

        var constraints = DependentRanges(state, packageId);
        var reasons = new List<string>();
        var incompatible = false;
        foreach (var (version, source) in candidates)
        {
            var blockedBy = constraints.Where(c => !c.Range.Satisfies(version)).ToList();
            if (blockedBy.Count > 0)
            {
                reasons.Add($"{packageId} {version}: {string.Join("; ", blockedBy.Select(c => $"{c.PluginId} needs {packageId} {c.Range.PrettyPrint()}"))}.");
                continue;
            }

            var target = await session.FetchAsync(source, new PackageIdentity(packageId, version)).ConfigureAwait(false);
            if (target is null)
            {
                reasons.Add($"The feed '{source.PackageSource.Source}' lists {packageId} {version} but doesn't serve it.");
                continue;
            }

            if (CheckPluginPackage(target.Metadata, requested: true) is { } notAPlugin)
            {
                reasons.Add(notAPlugin);
                continue;
            }

            if (HostCompatibility.Check(host, target.Metadata.DependencyGroups) is { } why)
            {
                reasons.Add($"{packageId} {version}: {why}");
                incompatible = true;
                continue;
            }

            if (version == current.Version)
            {
                return new InstallPlan { NoChange = $"{packageId} {version} is already installed.", Version = version };
            }

            return await PlanCellsAsync(state, target, current, cancellationToken).ConfigureAwait(false);
        }

        return InstallPlan.Fail(incompatible ? InstallStatus.Incompatible : InstallStatus.Failed,
            [$"No version of {packageId} newer than {current.Version} can be used.", .. reasons]);
    }

    /// <summary>The version ranges enabled plugins declare on a plugin (they must accept any version it switches to).</summary>
    public static IReadOnlyList<(string PluginId, VersionRange Range)> DependentRanges(StateDocument state, string pluginId) =>
        state.Plugins
            .Where(p => p.Enabled && !string.Equals(p.Id, pluginId, StringComparison.OrdinalIgnoreCase))
            .SelectMany(p => p.Dependencies
                .Where(d => d.Classification == DependencyClass.Plugin && string.Equals(d.Id, pluginId, StringComparison.OrdinalIgnoreCase))
                .SelectMany(d => d.RequestedBy.Select(r => (p.Id, r.Range))))
            .Distinct()
            .ToList();

    /// <summary>
    /// Steps 2 to 5 of a plan, for a chosen plugin package: new plugin dependencies, cells, joint resolution of every
    /// affected cell, dependency sets and downloads. <paramref name="replacing"/> is the installed entry an update replaces.
    /// </summary>
    private async Task<InstallPlan> PlanCellsAsync(StateDocument state, FeedPackage target, PluginState? replacing, CancellationToken cancellationToken)
    {
        var match = (Version: target.Identity.Version, Source: target.Source);

        // 2. New plugin dependencies, recursively. An update's plugin counts at its new version.
        var installed = new Dictionary<string, PluginState>(StringComparer.OrdinalIgnoreCase);
        foreach (var plugin in state.Plugins.Where(p => replacing is null || !string.Equals(p.Id, replacing.Id, StringComparison.OrdinalIgnoreCase)))
        {
            installed.TryAdd(plugin.Id, plugin);
        }

        var newPlugins = new Dictionary<string, FeedPackage>(StringComparer.OrdinalIgnoreCase) { [target.Identity.Id] = target };
        var closureErrors = await FindNewPluginsAsync(target, installed, newPlugins, cancellationToken).ConfigureAwait(false);
        if (closureErrors.Count > 0)
        {
            return InstallPlan.Fail(InstallStatus.Failed, closureErrors, match.Version);
        }

        // 3. Cells: the app's options, then the manifests, then a default cell.
        var enabled = installed.Values.Where(p => p.Enabled && !newPlugins.ContainsKey(p.Id)).ToList();
        var manifests = new Dictionary<string, PluginManifest?>(StringComparer.OrdinalIgnoreCase);
        foreach (var plugin in enabled)
        {
            manifests[plugin.Id] = _installed.GetMetadata(plugin.Id, plugin.Version)?.Manifest;
        }

        foreach (var plugin in newPlugins.Values)
        {
            manifests[plugin.Identity.Id] = plugin.Metadata.Manifest;
        }

        var assignment = CellAssigner.Assign(
            manifests.Select(m => new CellCandidate(m.Key, m.Value?.Cell, m.Value?.Isolated ?? false)),
            options.Cells);
        var cellErrors = assignment.Issues
            .Where(i => i.Severity == ValidationSeverity.Error && newPlugins.ContainsKey(i.PluginId))
            .Select(i => i.Message)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (cellErrors.Count > 0)
        {
            return InstallPlan.Fail(InstallStatus.Failed, cellErrors, match.Version);
        }

        var order = DependenciesFirst(newPlugins);
        var cells = order.Select(p => assignment.Cells[p.Identity.Id]).Distinct(StringComparer.Ordinal).ToList();
        if (replacing is not null)
        {
            // An update also changes the plugins depending on it: their cells are resolved again too.
            cells.AddRange(enabled
                .Where(p => p.Dependencies.Any(d => d.Classification == DependencyClass.Plugin && string.Equals(d.Id, replacing.Id, StringComparison.OrdinalIgnoreCase)))
                .Select(p => assignment.Cells[p.Id])
                .Where(c => !cells.Contains(c, StringComparer.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .ToList());
        }

        // 4. Resolve each affected cell jointly.
        var versions = enabled.ToDictionary(p => p.Id, p => p.Version, StringComparer.OrdinalIgnoreCase);
        foreach (var plugin in newPlugins.Values)
        {
            versions[plugin.Identity.Id] = plugin.Identity.Version;
        }

        var feedProviders = session.Repositories
            .Select(r => new FeedDependencyProvider(session, r, (id, versionRange) => host.IsFrameworkProvided(id, versionRange.MinVersion)))
            .ToList();
        var resolver = new CellResolver(session, [_installed, .. feedProviders], host.Framework, logger);
        var hostPins = HostPins();
        var incompatible = new List<string>();
        var errors = new List<string>();
        var resolutions = new Dictionary<string, (CellResolution Resolution, List<string> Members)>(StringComparer.Ordinal);
        foreach (var cell in cells)
        {
            var members = assignment.Members[cell].Order(StringComparer.OrdinalIgnoreCase).ToList();
            var memberSet = members.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var forcedPrivate = members
                .SelectMany(m => manifests.GetValueOrDefault(m)?.PrivateDependencies ?? [])
                .Where(id => !host.IsHostOwnedPackage(id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var pluginPins = versions.Where(v => !memberSet.Contains(v.Key)).ToDictionary(v => v.Key, v => v.Value, StringComparer.OrdinalIgnoreCase);
            var cellHostPins = hostPins
                .Where(p => !memberSet.Contains(p.Key) && !pluginPins.ContainsKey(p.Key) && !forcedPrivate.Contains(p.Key))
                .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);

            var resolution = await resolver.ResolveAsync(
                new CellResolutionRequest(cell, members.Select(m => new PackageIdentity(m, versions[m])).ToList(), cellHostPins, pluginPins, id => host.IsHostOwnedPackage(id)),
                cancellationToken).ConfigureAwait(false);
            incompatible.AddRange(resolution.Incompatibilities);
            errors.AddRange(resolution.Errors);
            resolutions[cell] = (resolution, members);
        }

        if (incompatible.Count > 0)
        {
            return InstallPlan.Fail(InstallStatus.Incompatible, incompatible.Concat(errors), match.Version);
        }

        if (errors.Count > 0)
        {
            return InstallPlan.Fail(InstallStatus.Failed, errors, match.Version);
        }

        // 5. Each plugin's resolved dependency set, and what has to be downloaded.
        var toRecord = new List<PluginToRecord>();
        var libraries = new Dictionary<PackageIdentity, FeedPackage>(PackageIdentityComparer.Default);
        foreach (var (cell, (resolution, members)) in resolutions)
        {
            foreach (var member in members)
            {
                var metadata = newPlugins.TryGetValue(member, out var fetched)
                    ? fetched.Metadata
                    : _installed.GetMetadata(member, versions[member]);
                if (metadata is null)
                {
                    errors.Add($"The installed plugin {member} {versions[member]} (cell '{cell}') isn't in the plugin root any more; reinstall it first.");
                    continue;
                }

                var dependencies = await DependencySetAsync(metadata, resolution, versions, libraries, errors).ConfigureAwait(false);
                var existing = newPlugins.ContainsKey(member) ? null : installed[member];
                if (existing is null || !SameDependencies(existing.Dependencies, dependencies))
                {
                    toRecord.Add(new PluginToRecord(metadata.Identity, existing, cell, dependencies));
                }
            }

            LogDiamonds(cell, toRecord.Where(p => string.Equals(p.Cell, cell, StringComparison.Ordinal)));
        }

        if (errors.Count > 0)
        {
            return InstallPlan.Fail(InstallStatus.Failed, errors.Distinct(StringComparer.Ordinal), match.Version);
        }

        var downloads = libraries.Values
            .OrderBy(p => p.Identity.Id, StringComparer.OrdinalIgnoreCase)
            .Concat(order)
            .Where(p => !_installed.IsExtracted(p.Identity.Id, p.Identity.Version))
            .ToList();

        // Dependencies first, so a plugin's records follow its plugin dependencies'.
        var rank = order.Select((p, i) => (p.Identity.Id, i)).ToDictionary(x => x.Id, x => x.i, StringComparer.OrdinalIgnoreCase);
        return new InstallPlan
        {
            Version = match.Version,
            NewPlugins = order.Select(p => p.Identity).ToList(),
            Plugins = toRecord.OrderBy(p => rank.GetValueOrDefault(p.Identity.Id, -1)).ToList(),
            Downloads = downloads,
            Cells = cells,
        };
    }

    /// <summary>Whether a package can be installed as a plugin of this app; the reason if not.</summary>
    private string? CheckPluginPackage(PackageMetadata metadata, bool requested)
    {
        var identity = $"{metadata.Identity.Id} {metadata.Identity.Version}";
        if (metadata.ManifestError is { } error)
        {
            return $"The plugin manifest of {identity} can't be read: {error}";
        }

        if (!metadata.IsPlugin)
        {
            return requested ? $"{identity} isn't a plugin package: it has no plugin manifest ({PluginManifest.GetFileName(metadata.Identity.Id)})." : null;
        }

        if (options.PluginPackageType is { Length: > 0 } type && !metadata.HasPackageType(type))
        {
            var types = metadata.PackageTypes.Count == 0 ? "none" : string.Join(", ", metadata.PackageTypes.Select(t => t.Name));
            return $"{identity} isn't a plugin package of this app: it doesn't have the package type {type} (its package types: {types}).";
        }

        return null;
    }

    /// <summary>Follows plugin dependencies that aren't installed yet: each gets the lowest applicable version (NuGet's rule).</summary>
    private async Task<List<string>> FindNewPluginsAsync(FeedPackage target, Dictionary<string, PluginState> installed, Dictionary<string, FeedPackage> newPlugins, CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        var pending = new Queue<FeedPackage>([target]);
        while (pending.TryDequeue(out var plugin))
        {
            foreach (var dependency in plugin.Metadata.DependenciesFor(host.Framework))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var needs = $"{plugin.Identity.Id} {plugin.Identity.Version} needs the plugin {dependency.Id} {dependency.VersionRange.PrettyPrint()}";
                if (host.IsHostOwnedPackage(dependency.Id, dependency.VersionRange.MinVersion) || host.FindPackage(dependency.Id) is not null)
                {
                    continue;   // the host's packages are never plugins
                }

                if (installed.TryGetValue(dependency.Id, out var existing))
                {
                    if (!existing.Enabled)
                    {
                        errors.Add($"{needs}, which is installed but disabled.");
                    }
                    else if (!dependency.VersionRange.Satisfies(existing.Version))
                    {
                        errors.Add($"{needs}, but version {existing.Version} is installed. Installing doesn't update an installed plugin.");
                    }

                    continue;
                }

                if (newPlugins.TryGetValue(dependency.Id, out var chosen))
                {
                    if (!dependency.VersionRange.Satisfies(chosen.Identity.Version))
                    {
                        errors.Add($"{needs}, but version {chosen.Identity.Version} was chosen for another plugin being installed.");
                    }

                    continue;
                }

                var candidates = await session.GetVersionsAsync(dependency.Id).ConfigureAwait(false);
                var best = dependency.VersionRange.FindBestMatch(candidates.Select(c => c.Version));
                if (best is null)
                {
                    continue;   // resolution reports it as not found
                }

                var package = await session.FetchAsync(candidates.First(c => c.Version == best).Source, new PackageIdentity(dependency.Id, best)).ConfigureAwait(false);
                if (package is null || !package.Metadata.IsPlugin)
                {
                    continue;   // a library: resolution handles it
                }

                if (CheckPluginPackage(package.Metadata, requested: false) is { } invalid)
                {
                    errors.Add(invalid);
                    continue;
                }

                newPlugins[package.Identity.Id] = package;
                pending.Enqueue(package);
            }
        }

        return errors;
    }

    /// <summary>
    /// A plugin's resolved dependency set: everything reachable from it through cell-private packages, each with its
    /// class and the ranges the plugin and its cell-private dependencies declare on it.
    /// </summary>
    private async Task<IReadOnlyList<DependencyRecord>> DependencySetAsync(
        PackageMetadata plugin,
        CellResolution resolution,
        IReadOnlyDictionary<string, NuGetVersion> plugins,
        Dictionary<PackageIdentity, FeedPackage> downloads,
        List<string> errors)
    {
        var records = new Dictionary<string, DependencyRecord>(StringComparer.OrdinalIgnoreCase);
        var requests = new Dictionary<string, List<DependencyRequest>>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<PackageMetadata>([plugin]);
        while (pending.TryDequeue(out var package))
        {
            foreach (var dependency in package.DependenciesFor(host.Framework))
            {
                var request = new DependencyRequest(package.Identity.Id, dependency.VersionRange);
                if (!requests.TryGetValue(dependency.Id, out var declared))
                {
                    requests[dependency.Id] = declared = [];
                }

                if (!declared.Contains(request))
                {
                    declared.Add(request);
                }

                if (records.ContainsKey(dependency.Id))
                {
                    continue;
                }

                var id = dependency.Id;
                if (host.IsFrameworkProvided(id, dependency.VersionRange.MinVersion))
                {
                    // Skipped by resolution (the SDK prunes these); recorded as host-owned, never version-checked.
                    records[id] = new DependencyRecord(id, dependency.VersionRange.MinVersion ?? new NuGetVersion(0, 0, 0), DependencyClass.HostOwned);
                }
                else if (plugins.TryGetValue(id, out var pluginVersion))
                {
                    records[id] = new DependencyRecord(id, pluginVersion, DependencyClass.Plugin);
                }
                else if (!resolution.Packages.TryGetValue(id, out var resolved))
                {
                    continue;   // reported by resolution
                }
                else if (resolution.HostPinned.Contains(id))
                {
                    records[id] = new DependencyRecord(host.FindPackage(id)?.Id ?? id, resolved.Version,
                        host.IsHostOwnedPackage(id) ? DependencyClass.HostOwned : DependencyClass.HostShared);
                }
                else if (host.IsHostOwnedPackage(id))
                {
                    // Host-owned but not provided by the host: validation reports it (HostOwnedMissing), nothing is downloaded.
                    records[id] = new DependencyRecord(id, resolved.Version, DependencyClass.HostOwned);
                }
                else
                {
                    var metadata = await MetadataAsync(resolved, downloads).ConfigureAwait(false);
                    if (metadata is null)
                    {
                        errors.Add($"{id} {resolved.Version} was resolved but can't be read from '{resolved.Provider?.Source.Source}'.");
                        continue;
                    }

                    if (metadata.IsPlugin)
                    {
                        errors.Add($"{metadata.Identity.Id} {metadata.Identity.Version} is a plugin package, but {plugin.Identity.Id} reaches it only through the library {package.Identity.Id}; plugin dependencies must be declared by plugins.");
                        continue;
                    }

                    records[id] = new DependencyRecord(metadata.Identity.Id, metadata.Identity.Version, DependencyClass.CellPrivate);
                    pending.Enqueue(metadata);
                }
            }
        }

        return records.Values
            .Select(r => r with { RequestedBy = requests[r.Id] })
            .OrderBy(r => r.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<PackageMetadata?> MetadataAsync(ResolvedPackage package, Dictionary<PackageIdentity, FeedPackage> downloads)
    {
        switch (package.Provider)
        {
            case InstalledDependencyProvider:
                return _installed.GetMetadata(package.Id, package.Version);
            case FeedDependencyProvider feed:
                var fetched = await feed.FetchAsync(new PackageIdentity(package.Id, package.Version)).ConfigureAwait(false);
                if (fetched is not null)
                {
                    downloads.TryAdd(fetched.Identity, fetched);
                }

                return fetched?.Metadata;
            default:
                return null;
        }
    }

    /// <summary>The host's packages (and projects with assemblies) at the host's versions, without the app itself.</summary>
    private Dictionary<string, NuGetVersion> HostPins()
    {
        var app = Path.GetFileName(host.AppDepsFile);
        app = app.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase) ? app[..^".deps.json".Length] : app;
        return host.Packages.Values
            .Where(p => p.Version is not null && (p.IsPackage || p.Assemblies.Count > 0) && !string.Equals(p.Id, app, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(p => p.Id, p => p.Version!, StringComparer.OrdinalIgnoreCase);
    }

    private static List<FeedPackage> DependenciesFirst(Dictionary<string, FeedPackage> plugins)
    {
        var order = new List<FeedPackage>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var plugin in plugins.Values)
        {
            Visit(plugin);
        }

        return order;

        void Visit(FeedPackage plugin)
        {
            if (!visited.Add(plugin.Identity.Id))
            {
                return;
            }

            foreach (var dependency in plugin.Metadata.DependencyGroups.SelectMany(g => g.Packages))
            {
                if (plugins.TryGetValue(dependency.Id, out var other))
                {
                    Visit(other);
                }
            }

            order.Add(plugin);
        }
    }

    private static bool SameDependencies(IReadOnlyList<ResolvedDependency> current, IReadOnlyList<DependencyRecord> resolved) =>
        current.Count == resolved.Count
        && current.All(c => resolved.Any(r =>
            string.Equals(r.Id, c.Id, StringComparison.OrdinalIgnoreCase)
            && r.Version == c.Version
            && r.Classification == c.Classification
            && r.RequestedBy.Count == c.RequestedBy.Count
            && r.RequestedBy.All(c.RequestedBy.Contains)));

    private void LogDiamonds(string cell, IEnumerable<PluginToRecord> plugins)
    {
        foreach (var group in plugins.SelectMany(p => p.Dependencies).Where(d => d.Classification == DependencyClass.CellPrivate).GroupBy(d => d.Id, StringComparer.OrdinalIgnoreCase))
        {
            var ranges = group.SelectMany(d => d.RequestedBy).Select(r => r.Range).Distinct().ToList();
            if (ranges.Count > 1)
            {
                FeedLog.Diamond(logger, cell, group.Key, string.Join(", ", ranges.Select(r => r.PrettyPrint())), group.First().Version);
            }
        }
    }

    private string FeedList()
    {
        var rejected = session.DisallowedFeeds.Count == 0 ? "" : $"; the trust policy doesn't allow {string.Join(", ", session.DisallowedFeeds)}";
        return session.Repositories.Count == 0
            ? $"any feed (none is configured{rejected})"
            : $"the feeds ({string.Join(", ", session.Repositories.Select(r => r.PackageSource.Source))}{rejected})";
    }
}

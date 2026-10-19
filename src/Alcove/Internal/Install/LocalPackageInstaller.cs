using Alcove.Internal.Hosting;
using Alcove.Internal.IO;
using Alcove.Internal.State;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Versioning;

namespace Alcove.Internal.Install;

/// <summary>
/// Installs plugins into a plugin root from a set of <c>.nupkg</c> files, without a feed: extracts them,
/// records them with <see cref="PackageRecorder"/> and writes the state file. Test infrastructure only: real
/// installs go through <see cref="PluginInstaller"/> (feeds, the dependency walker, trust, hot load).
/// </summary>
/// <remarks>
/// Resolution is deliberately simple, not NuGet's dependency walker: each dependency gets the lowest version
/// in the set that satisfies its range (the highest when several ranges meet), a host package that satisfies
/// the range is host-shared at the host's version, host-owned packages are recorded but not needed, and a
/// dependency that is itself a plugin package (it has a plugin manifest) is installed as a plugin. Every
/// declared range is recorded with the dependency (<see cref="ResolvedDependency.RequestedBy"/>).
/// </remarks>
internal sealed class LocalPackageInstaller
{
    private readonly HostInventory _host;
    private readonly PackageLayout _layout;
    private readonly PackageRecorder _recorder;

    public LocalPackageInstaller(HostInventory host, string root)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
        _layout = new PackageLayout(root);
        _recorder = new PackageRecorder(host, _layout);
    }

    /// <summary>
    /// Installs the plugins <paramref name="pluginIds"/> (the highest version in the set, stable preferred) and
    /// their plugin dependencies from <paramref name="packageFiles"/>, and writes them to the state file.
    /// </summary>
    /// <returns>The state entries written, dependencies first.</returns>
    /// <exception cref="InvalidOperationException">A plugin or a dependency can't be resolved from the set or the host.</exception>
    public async Task<IReadOnlyList<PluginState>> InstallAsync(IEnumerable<string> packageFiles, IEnumerable<string> pluginIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packageFiles);
        ArgumentNullException.ThrowIfNull(pluginIds);
        var index = Index(packageFiles);
        var resolved = new Dictionary<string, (PackageEntry Plugin, List<DependencyRecord> Dependencies)>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var id in pluginIds)
        {
            var plugin = Latest(index, id) ?? throw new InvalidOperationException($"The plugin {id} isn't in the package set.");
            ResolvePlugin(plugin, index, resolved, order);
        }

        var states = new List<PluginState>();
        foreach (var id in order)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (plugin, dependencies) = resolved[id];
            _recorder.Extract(plugin.Path);
            var records = new List<DependencyRecord>();
            foreach (var dependency in dependencies)
            {
                if (dependency.Classification == DependencyClass.CellPrivate)
                {
                    _recorder.Extract(index[dependency.Id].Single(e => e.Version == dependency.Version).Path);
                    records.Add(dependency with { Package = _recorder.Record(dependency.Id, dependency.Version) });
                }
                else
                {
                    records.Add(dependency);
                }
            }

            states.Add(_recorder.CreatePluginState(_recorder.Record(plugin.Id, plugin.Version), records));
        }

        await _recorder.WriteAsync(states, cancellationToken).ConfigureAwait(false);
        return states;
    }

    /// <summary>Installs every plugin package in <paramref name="folder"/>.</summary>
    public Task<IReadOnlyList<PluginState>> InstallAllAsync(string folder, CancellationToken cancellationToken = default)
    {
        var files = Directory.GetFiles(folder, "*.nupkg");
        var plugins = Index(files).Values.Where(v => v.Any(e => e.IsPlugin)).Select(v => v[0].Id).Order(StringComparer.OrdinalIgnoreCase).ToList();
        return InstallAsync(files, plugins, cancellationToken);
    }

    private void ResolvePlugin(PackageEntry plugin, Dictionary<string, List<PackageEntry>> index, Dictionary<string, (PackageEntry, List<DependencyRecord>)> resolved, List<string> order)
    {
        if (resolved.ContainsKey(plugin.Id))
        {
            return;
        }

        var dependencies = new Dictionary<string, DependencyRecord>(StringComparer.OrdinalIgnoreCase);
        var requests = new Dictionary<string, List<DependencyRequest>>(StringComparer.OrdinalIgnoreCase);
        var pluginDependencies = new List<PackageEntry>();
        resolved[plugin.Id] = (plugin, []);   // placeholder against cycles
        var pending = new Queue<(string Id, VersionRange Range, string NeededBy)>(plugin.Dependencies.Select(d => (d.Id, d.VersionRange, plugin.Id)));
        while (pending.TryDequeue(out var next))
        {
            var (id, range, neededBy) = next;
            if (!requests.TryGetValue(id, out var declared))
            {
                requests[id] = declared = [];
            }

            var request = new DependencyRequest(neededBy, range);
            if (!declared.Contains(request))
            {
                declared.Add(request);
            }

            if (_host.IsHostOwnedPackage(id, range.MinVersion))
            {
                var version = range.MinVersion ?? _host.FindPackage(id)?.Version ?? new NuGetVersion(0, 0, 0);
                AddHighest(dependencies, new DependencyRecord(id, version, DependencyClass.HostOwned));
                continue;
            }

            var candidates = index.GetValueOrDefault(id) ?? [];
            if (candidates.Any(c => c.IsPlugin))
            {
                var dependency = Lowest(candidates, range)
                    ?? throw new InvalidOperationException($"{neededBy} needs the plugin {id} {range}, which isn't in the package set.");
                AddHighest(dependencies, new DependencyRecord(id, dependency.Version, DependencyClass.Plugin));
                pluginDependencies.Add(dependency);
                continue;
            }

            var forcedPrivate = plugin.Manifest?.PrivateDependencies.Contains(id, StringComparer.OrdinalIgnoreCase) == true;
            if (!forcedPrivate && _host.FindPackage(id)?.Version is { } hostVersion && range.Satisfies(hostVersion))
            {
                AddHighest(dependencies, new DependencyRecord(id, hostVersion, DependencyClass.HostShared));
                continue;
            }

            var package = Lowest(candidates, range)
                ?? throw new InvalidOperationException($"{neededBy} needs {id} {range}, which neither the package set nor the host has.");
            if (AddHighest(dependencies, new DependencyRecord(id, package.Version, DependencyClass.CellPrivate)))
            {
                foreach (var transitive in package.Dependencies)
                {
                    pending.Enqueue((transitive.Id, transitive.VersionRange, id));
                }
            }
        }

        resolved[plugin.Id] = (plugin, dependencies.Values
            .Select(d => d with { RequestedBy = requests[d.Id] })
            .OrderBy(d => d.Id, StringComparer.OrdinalIgnoreCase)
            .ToList());
        foreach (var dependency in pluginDependencies)
        {
            ResolvePlugin(dependency, index, resolved, order);
        }

        order.Add(plugin.Id);
    }

    /// <summary>Keeps the highest version per ID (a host-owned or host-shared answer wins over a private one only when equal or higher). Returns whether it changed.</summary>
    private static bool AddHighest(Dictionary<string, DependencyRecord> dependencies, DependencyRecord record)
    {
        if (dependencies.TryGetValue(record.Id, out var existing) && existing.Version >= record.Version)
        {
            return false;
        }

        dependencies[record.Id] = record;
        return true;
    }

    private static PackageEntry? Lowest(IEnumerable<PackageEntry> candidates, VersionRange range) =>
        candidates.Where(c => range.Satisfies(c.Version)).MinBy(c => c.Version);

    private static PackageEntry? Latest(Dictionary<string, List<PackageEntry>> index, string id)
    {
        var candidates = index.GetValueOrDefault(id) ?? [];
        return candidates.Where(c => !c.Version.IsPrerelease).MaxBy(c => c.Version) ?? candidates.MaxBy(c => c.Version);
    }

    private Dictionary<string, List<PackageEntry>> Index(IEnumerable<string> packageFiles)
    {
        var index = new Dictionary<string, List<PackageEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in packageFiles)
        {
            using var reader = new PackageArchiveReader(path);
            var identity = reader.GetIdentity();
            var manifestName = PluginManifest.GetFileName(identity.Id);
            PluginManifest? manifest = null;
            if (reader.GetFiles().Contains(manifestName, StringComparer.OrdinalIgnoreCase))
            {
                using var stream = reader.GetStream(reader.GetFiles().First(f => string.Equals(f, manifestName, StringComparison.OrdinalIgnoreCase)));
                manifest = PluginManifest.Read(stream);
            }

            var groups = reader.GetPackageDependencies().ToList();
            var nearest = NuGetFrameworkUtility.GetNearest(groups, _host.Framework, g => g.TargetFramework);
            var entry = new PackageEntry(identity.Id, identity.Version, Path.GetFullPath(path), manifest, nearest?.Packages.ToList() ?? []);
            if (!index.TryGetValue(identity.Id, out var list))
            {
                index[identity.Id] = list = [];
            }

            list.Add(entry);
        }

        return index;
    }

    private sealed record PackageEntry(string Id, NuGetVersion Version, string Path, PluginManifest? Manifest, IReadOnlyList<NuGet.Packaging.Core.PackageDependency> Dependencies)
    {
        public bool IsPlugin => Manifest is not null;
    }
}

using Alcove.Internal.Assets;
using Alcove.Internal.Cells;
using Alcove.Internal.Hosting;
using Alcove.Internal.IO;
using Alcove.Internal.State;
using NuGet.Versioning;

namespace Alcove.Internal.Classification;

/// <summary>
/// Classifies every assembly of every cell (architecture §4) from the host inventory, the state file's
/// resolved dependency sets, the plugins' manifests (private dependencies) and the plugin dependencies, and
/// builds each cell's <see cref="CellPlan"/>. Resolution (computing the dependency sets from feeds) happens at
/// install; here they come from the state file.
/// </summary>
/// <remarks>
/// Precedence follows the load order: host-owned, then plugin-provided (the transitive plugin-dependency
/// closure plus each provider's exposed dependencies), then cell-private, then host-shared.
/// </remarks>
internal sealed class AssemblyClassifier
{
    private readonly HostInventory _host;
    private readonly PackageLayout _layout;
    private readonly AssetSelector _assets;
    private readonly Func<PluginState, PluginManifest?> _manifests;

    /// <summary>Creates a classifier.</summary>
    /// <param name="host">The host inventory (framework, RID, packages, host-owned set).</param>
    /// <param name="layout">The plugin root's package layout.</param>
    /// <param name="assets">Asset selection; <see cref="AssetSelector.Default"/> if omitted.</param>
    /// <param name="manifests">Reads a plugin's manifest; by default from its package folder (a missing or invalid manifest counts as none).</param>
    public AssemblyClassifier(HostInventory host, PackageLayout layout, AssetSelector? assets = null, Func<PluginState, PluginManifest?>? manifests = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(layout);
        _host = host;
        _layout = layout;
        _assets = assets ?? AssetSelector.Default;
        _manifests = manifests ?? ReadManifest;
    }

    /// <summary>The cell a plugin is in: its recorded assignment, or a default cell named after the plugin.</summary>
    public static string CellOf(PluginState plugin) => string.IsNullOrWhiteSpace(plugin.Cell) ? plugin.Id : plugin.Cell;

    /// <summary>Classifies the cells of every enabled plugin in the state.</summary>
    public IReadOnlyList<CellClassification> Classify(StateDocument state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var plugins = state.Plugins.Where(p => p.Enabled).ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
        var cells = plugins.Values
            .GroupBy(CellOf, StringComparer.Ordinal)
            .Select(g => new CellBuilder(g.Key, g.OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase).ToList()))
            .ToDictionary(c => c.Name, StringComparer.Ordinal);

        // Pass 1: each cell's own assemblies (its plugins, their private and host-provided dependencies).
        foreach (var cell in cells.Values)
        {
            ClassifyOwn(cell);
        }

        // Pass 2: plugin-provided assemblies, which need the providers' cells.
        foreach (var cell in cells.Values)
        {
            ClassifyProvided(cell, plugins, cells);
        }

        return cells.Values.OrderBy(c => c.Name, StringComparer.Ordinal).Select(c => c.Build()).ToList();
    }

    private void ClassifyOwn(CellBuilder cell)
    {
        var privatePackages = new Dictionary<string, (NuGetVersion Version, PluginState Owner, IReadOnlyList<AssemblyRecord> Records)>(StringComparer.OrdinalIgnoreCase);
        var pluginIds = cell.Plugins.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var plugin in cell.Plugins)
        {
            cell.Plan.Plugins.Add(plugin.Id);
            AddPrivate(cell, privatePackages, plugin, plugin.Id, plugin.Version, plugin.Assemblies);

            var privateDependencies = (_manifests(plugin)?.PrivateDependencies ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var dependency in plugin.Dependencies)
            {
                if (dependency.Classification == DependencyClass.Plugin)
                {
                    // A plugin dependency in the same cell is just another plugin of the cell; others are pass 2.
                    continue;
                }

                if (_host.IsHostOwnedPackage(dependency.Id, dependency.Version))
                {
                    AddHostOwned(cell, plugin, dependency);
                    continue;
                }

                var hostPackage = _host.FindPackage(dependency.Id);
                var isPrivate = dependency.Classification == DependencyClass.CellPrivate
                    || privateDependencies.Contains(dependency.Id)
                    || hostPackage is null;
                if (!isPrivate && hostPackage!.Version is { } hostVersion && hostVersion < dependency.Version)
                {
                    cell.Issue(ClassificationIssueKind.HostSharedDowngraded, plugin.Id, dependency.Id,
                        $"{plugin.Id} resolved {dependency.Id} {dependency.Version} from the host, but the host now has {hostVersion}; the plugin needs to be resolved again.");
                    isPrivate = Directory.Exists(_layout.GetPackageDirectory(dependency.Id, dependency.Version));
                }

                if (isPrivate)
                {
                    AddPrivate(cell, privatePackages, plugin, dependency.Id, dependency.Version, dependency.Assemblies);
                }
                else
                {
                    foreach (var assembly in hostPackage!.Assemblies)
                    {
                        cell.Plan.HostShared.Add(assembly.Name);
                        cell.Add(new ClassifiedAssembly(assembly.Name, AssemblyClass.HostShared, hostPackage.Id, hostPackage.Version));
                    }
                }
            }
        }

        foreach (var (id, (version, owner, records)) in privatePackages)
        {
            AddPrivateAssets(cell, owner, id, version, records, isPlugin: pluginIds.Contains(id));
        }
    }

    private void AddHostOwned(CellBuilder cell, PluginState plugin, ResolvedDependency dependency)
    {
        if (_host.IsFrameworkProvided(dependency.Id, dependency.Version))
        {
            // Skipped, not version-checked (matches the SDK's pruning). The inventory answers these names.
            return;
        }

        var hostPackage = _host.FindPackage(dependency.Id);
        if (hostPackage is null)
        {
            cell.Issue(ClassificationIssueKind.HostOwnedMissing, plugin.Id, dependency.Id,
                $"{plugin.Id} needs the host-owned package {dependency.Id} {dependency.Version}, which the host doesn't have.");
            cell.Plan.HostOwned.Add(dependency.Id);
            cell.Add(new ClassifiedAssembly(dependency.Id, AssemblyClass.HostOwned, dependency.Id, null));
            return;
        }

        if (hostPackage.Version is { } hostVersion && hostVersion < dependency.Version)
        {
            cell.Issue(ClassificationIssueKind.HostOwnedConflict, plugin.Id, dependency.Id,
                $"{plugin.Id} needs the host-owned package {dependency.Id} {dependency.Version}, but the host has {hostVersion}.");
        }

        foreach (var assembly in hostPackage.Assemblies)
        {
            cell.Plan.HostOwned.Add(assembly.Name);
            cell.Add(new ClassifiedAssembly(assembly.Name, AssemblyClass.HostOwned, hostPackage.Id, hostPackage.Version));
        }
    }

    private static void AddPrivate(
        CellBuilder cell,
        Dictionary<string, (NuGetVersion Version, PluginState Owner, IReadOnlyList<AssemblyRecord> Records)> privatePackages,
        PluginState owner,
        string id,
        NuGetVersion version,
        IReadOnlyList<AssemblyRecord> records)
    {
        if (privatePackages.TryGetValue(id, out var existing))
        {
            if (existing.Version == version)
            {
                return;
            }

            var (high, low) = existing.Version > version ? (existing.Version, version) : (version, existing.Version);
            cell.Issue(ClassificationIssueKind.CellVersionConflict, owner.Id, id,
                $"Cell '{cell.Name}' resolved {id} {existing.Version} for {existing.Owner.Id} and {version} for {owner.Id}; a cell holds one version, so {high} is used instead of {low}.");
            if (existing.Version > version)
            {
                return;
            }
        }

        privatePackages[id] = (version, owner, records);
    }

    private void AddPrivateAssets(CellBuilder cell, PluginState owner, string id, NuGetVersion version, IReadOnlyList<AssemblyRecord> records, bool isPlugin)
    {
        var directory = _layout.GetPackageDirectory(id, version);
        if (!Directory.Exists(directory))
        {
            cell.Issue(ClassificationIssueKind.PackageMissing, owner.Id, id,
                $"{(isPlugin ? "The plugin package" : "The cell-private package")} {id} {version} isn't in the plugin root ('{directory}').");
            return;
        }

        var wpfRelevant = records.Where(r => r.WpfRelevant).Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var assets = _assets.SelectFromDirectory(directory, _host.Framework, _host.RuntimeIdentifier);
        foreach (var relative in assets.RuntimeAssemblies)
        {
            var name = Path.GetFileNameWithoutExtension(relative);
            var path = AssetPath(directory, relative);
            var isWpfRelevant = wpfRelevant.Contains(name);
            cell.Plan.Private[name] = new PrivateAssembly(name, path, id, version) { WpfRelevant = isWpfRelevant };
            cell.Add(new ClassifiedAssembly(name, AssemblyClass.CellPrivate, id, version, path) { WpfRelevant = isWpfRelevant });
        }

        foreach (var satellite in assets.ResourceAssemblies)
        {
            var name = Path.GetFileNameWithoutExtension(satellite.Path);
            cell.Plan.Satellites[CellPlan.SatelliteKey(name, satellite.Culture)] =
                new SatelliteAssembly(name, satellite.Culture, AssetPath(directory, satellite.Path), id, version);
        }

        foreach (var native in assets.NativeLibraries)
        {
            var fileName = Path.GetFileName(native);
            cell.Plan.NativeLibraries[fileName] = new NativeLibraryAsset(fileName, AssetPath(directory, native), id, version);
        }
    }

    /// <summary>
    /// Joins a package directory and a NuGet asset path (always <c>/</c>-separated) with the platform's separator, so
    /// plan paths compare equal to paths built elsewhere (answers are compared as strings).
    /// </summary>
    private static string AssetPath(string directory, string relative) =>
        Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar));

    private static void ClassifyProvided(CellBuilder cell, Dictionary<string, PluginState> plugins, Dictionary<string, CellBuilder> cells)
    {
        foreach (var (provider, dependent) in PluginClosure(cell, plugins))
        {
            if (provider is null)
            {
                continue;
            }

            var providerCell = cells[CellOf(provider)];
            if (ReferenceEquals(providerCell, cell))
            {
                continue;
            }

            // The provider's own assemblies.
            foreach (var assembly in providerCell.Assemblies.Where(a => a.Class == AssemblyClass.CellPrivate && string.Equals(a.PackageId, provider.Id, StringComparison.OrdinalIgnoreCase)))
            {
                cell.Provide(new ClassifiedAssembly(assembly.Name, AssemblyClass.PluginProvided, provider.Id, provider.Version, ProviderCell: providerCell.Name, ProviderPlugin: provider.Id));
            }

            // Its exposed dependencies, whatever class they have in the provider's cell.
            foreach (var exposed in provider.ExposedDependencies)
            {
                var source = providerCell.Find(exposed);
                var consumerCopy = cell.Find(exposed);
                if (consumerCopy is { PackageVersion: { } needed } && source is { PackageVersion: { } available } && needed > available)
                {
                    cell.Issue(ClassificationIssueKind.ExposedDependencyConflict, dependent, consumerCopy.PackageId,
                        $"{dependent} needs {consumerCopy.PackageId} {needed}, but {provider.Id} exposes {exposed} from cell '{providerCell.Name}', which has {source.PackageId} {available}.");
                }

                cell.Provide(new ClassifiedAssembly(exposed, AssemblyClass.PluginProvided, source?.PackageId ?? exposed, source?.PackageVersion, ProviderCell: providerCell.Name, ProviderPlugin: provider.Id));
            }
        }

        foreach (var missing in cell.Plugins.SelectMany(p => p.Dependencies
            .Where(d => d.Classification == DependencyClass.Plugin && !plugins.ContainsKey(d.Id))
            .Select(d => (Plugin: p.Id, Dependency: d))))
        {
            cell.Issue(ClassificationIssueKind.PluginDependencyMissing, missing.Plugin, missing.Dependency.Id,
                $"{missing.Plugin} depends on the plugin {missing.Dependency.Id} {missing.Dependency.Version}, which isn't installed or is disabled.");
        }
    }

    /// <summary>The transitive plugin-dependency closure of a cell's plugins: (provider, the cell plugin that needs it).</summary>
    private static IEnumerable<(PluginState? Provider, string Dependent)> PluginClosure(CellBuilder cell, Dictionary<string, PluginState> plugins)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<(string Id, string Dependent)>();
        foreach (var plugin in cell.Plugins)
        {
            seen.Add(plugin.Id);
            foreach (var dependency in plugin.Dependencies.Where(d => d.Classification == DependencyClass.Plugin))
            {
                pending.Enqueue((dependency.Id, plugin.Id));
            }
        }

        while (pending.TryDequeue(out var next))
        {
            if (!seen.Add(next.Id) || !plugins.TryGetValue(next.Id, out var provider))
            {
                continue;
            }

            yield return (provider, next.Dependent);
            foreach (var dependency in provider.Dependencies.Where(d => d.Classification == DependencyClass.Plugin))
            {
                pending.Enqueue((dependency.Id, next.Dependent));
            }
        }
    }

    private PluginManifest? ReadManifest(PluginState plugin)
    {
        var path = _layout.GetManifestPath(plugin.Id, plugin.Version);
        try
        {
            using var stream = File.OpenRead(path);
            return PluginManifest.Read(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            // Validation reports missing or invalid manifests; classification treats them as "no private dependencies".
            return null;
        }
    }

    private sealed class CellBuilder(string name, IReadOnlyList<PluginState> plugins)
    {
        private readonly Dictionary<string, ClassifiedAssembly> _assemblies = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<ClassificationIssue> _issues = [];

        public string Name { get; } = name;

        public IReadOnlyList<PluginState> Plugins { get; } = plugins;

        public CellPlan Plan { get; } = new();

        public IEnumerable<ClassifiedAssembly> Assemblies => _assemblies.Values;

        public ClassifiedAssembly? Find(string assemblyName) => _assemblies.GetValueOrDefault(assemblyName);

        /// <summary>Adds an assembly of the cell's own plugins; a higher-precedence class already recorded wins.</summary>
        public void Add(ClassifiedAssembly assembly)
        {
            if (_assemblies.TryGetValue(assembly.Name, out var existing) && Rank(existing.Class) <= Rank(assembly.Class))
            {
                return;
            }

            _assemblies[assembly.Name] = assembly;
        }

        /// <summary>Adds a plugin-provided assembly: it beats the cell's own copy unless that's host-owned.</summary>
        public void Provide(ClassifiedAssembly assembly)
        {
            if (_assemblies.TryGetValue(assembly.Name, out var existing) && existing.Class is AssemblyClass.HostOwned or AssemblyClass.PluginProvided)
            {
                return;
            }

            _assemblies[assembly.Name] = assembly;
            Plan.Private.Remove(assembly.Name);
            Plan.HostShared.Remove(assembly.Name);
            Plan.Provided[assembly.Name] = assembly.ProviderCell!;
        }

        public void Issue(ClassificationIssueKind kind, string pluginId, string packageId, string message) =>
            _issues.Add(new ClassificationIssue(kind, Name, pluginId, packageId, message));

        public CellClassification Build()
        {
            // Precedence: a host-owned name is never private or shared, a private name is never host-shared.
            foreach (var hostOwned in Plan.HostOwned)
            {
                Plan.Private.Remove(hostOwned);
                Plan.HostShared.Remove(hostOwned);
                Plan.Provided.Remove(hostOwned);
            }

            foreach (var name in Plan.Private.Keys)
            {
                Plan.HostShared.Remove(name);
            }

            return new CellClassification(
                Name,
                Plugins.Select(p => p.Id).ToList(),
                _assemblies.Values.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList(),
                Plan,
                _issues.ToList());
        }

        private static int Rank(AssemblyClass assemblyClass) => assemblyClass switch
        {
            AssemblyClass.HostOwned => 0,
            AssemblyClass.PluginProvided => 1,
            AssemblyClass.CellPrivate => 2,
            _ => 3,
        };
    }
}

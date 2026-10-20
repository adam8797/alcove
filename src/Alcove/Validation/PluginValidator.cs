using System.Text.Json;
using Alcove.Cells;
using Alcove.Hosting;
using Alcove.Inspection;
using Alcove.Loading;
using Alcove.Packaging;
using Alcove.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Alcove.Validation;

/// <summary>
/// Validation (architecture §6): local checks only, never the network. Reads the state file, checks it against
/// the disk (sizes and timestamps), the manifests, the plugin dependency graph, the cell rules, the host
/// (host-owned conflicts) and the classification (exposed-dependency conflicts), then looks for WPF and native
/// file-name duplicates across cells. Errors block the plugins concerned and their dependents; the rest load.
/// </summary>
internal sealed class PluginValidator
{
    private const int MaxFilesListed = 3;

    private readonly HostInventory _host;
    private readonly PackageLayout _layout;
    private readonly IReadOnlyList<CellDefinition> _appCells;
    private readonly AssetSelector _assets;
    private readonly Func<string, string?> _hostAssemblyPath;
    private readonly Lazy<HostAssemblyFiles> _hostFiles;
    private readonly ILogger _logger;

    /// <summary>Creates a validator.</summary>
    /// <param name="host">The host inventory.</param>
    /// <param name="layout">The plugin root.</param>
    /// <param name="appCells">The app's cell definitions.</param>
    /// <param name="loggerFactory">Logging.</param>
    /// <param name="assets">Asset selection; <see cref="AssetSelector.Default"/> if omitted.</param>
    /// <param name="hostAssemblyPath">Finds the file of a host assembly by simple name, for WPF duplicate checks against the default context.</param>
    public PluginValidator(
        HostInventory host,
        PackageLayout layout,
        IEnumerable<CellDefinition> appCells,
        ILoggerFactory? loggerFactory = null,
        AssetSelector? assets = null,
        Func<string, string?>? hostAssemblyPath = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(appCells);
        _host = host;
        _layout = layout;
        _appCells = appCells.ToList();
        _assets = assets ?? AssetSelector.Default;
        _hostAssemblyPath = hostAssemblyPath ?? TrustedPlatformAssemblies.Find;
        _hostFiles = new Lazy<HostAssemblyFiles>(() => HostAssemblyFiles.FromCurrentProcess(host));
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger("Alcove.Validation");
    }

    /// <summary>Validates the plugin root and plans the load.</summary>
    public LoadPlan Validate(CancellationToken cancellationToken = default)
    {
        StateDocument state;
        try
        {
            state = new StateStore(_layout.Root).Read();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            var issues = new IssueList();
            issues.Add(ValidationSeverity.Error, ValidationCodes.StateFileUnreadable, ex.Message);
            return Finish(issues, [], [], []);
        }

        return Validate(state, cancellationToken);
    }

    /// <summary>Validates a given state (e.g. the would-be state of an install) against the plugin root and plans the load.</summary>
    public LoadPlan Validate(StateDocument state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        var issues = new IssueList();
        var all = new Dictionary<string, PluginState>(StringComparer.OrdinalIgnoreCase);
        foreach (var plugin in state.Plugins)
        {
            if (!all.TryAdd(plugin.Id, plugin))
            {
                issues.Add(ValidationSeverity.Warning, ValidationCodes.StateFileUnreadable,
                    $"The state file lists {plugin.Id} more than once; the first entry ({all[plugin.Id].Version}) is used.", plugin.Id);
            }
        }

        var enabled = all.Values.Where(p => p.Enabled).ToList();
        var manifests = new Dictionary<string, PluginManifest?>(StringComparer.OrdinalIgnoreCase);
        foreach (var plugin in enabled)
        {
            cancellationToken.ThrowIfCancellationRequested();
            manifests[plugin.Id] = CheckPackages(plugin, issues);
            CheckHostRanges(plugin, issues);
        }

        CheckDependencyGraph(enabled, all, issues);

        var assignment = CellAssigner.Assign(
            enabled.Select(p => new CellCandidate(p.Id, manifests[p.Id]?.Cell, manifests[p.Id]?.Isolated ?? false)),
            _appCells);
        foreach (var issue in assignment.Issues)
        {
            issues.Add(issue.Severity, issue.Code, issue.Message, issue.PluginId, issue.Cell);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var classifications = Classify(enabled, assignment, manifests);
        // Host-owned dependencies with recorded ranges were checked against their ranges (CheckHostRanges); the
        // classifier's "host version lower than resolved" check only stands in for state files without ranges.
        var rangeChecked = enabled
            .SelectMany(p => p.Dependencies.Where(d => d.RequestedBy.Count > 0).Select(d => (p.Id, d.Id)))
            .ToHashSet();
        foreach (var issue in classifications.SelectMany(c => c.Issues))
        {
            if (issue.Kind == ClassificationIssueKind.HostOwnedConflict && rangeChecked.Contains((issue.PluginId, issue.PackageId)))
            {
                continue;
            }

            AddClassificationIssue(issue, issues);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var discovered = DiscoverEntryPoints(enabled, manifests, assignment, classifications, issues);

        // Errors block a plugin, and everything that depends on it (directly or not).
        var blocked = issues.BlockedPlugins();
        var dependents = enabled.ToDictionary(p => p.Id, p => PluginDependencies(p).ToList(), StringComparer.OrdinalIgnoreCase);
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var plugin in enabled.Where(p => !blocked.ContainsKey(p.Id)))
            {
                var dependency = dependents[plugin.Id].FirstOrDefault(blocked.ContainsKey);
                if (dependency is null)
                {
                    continue;
                }

                var message = $"{plugin.Id} depends on the plugin {dependency}, which can't load: {blocked[dependency]}";
                issues.Add(ValidationSeverity.Error, ValidationCodes.PluginDependencyNotLoadable, message, plugin.Id, assignment.Cells[plugin.Id]);
                blocked[plugin.Id] = message;
                changed = true;
            }
        }

        var loadable = enabled.Where(p => !blocked.ContainsKey(p.Id)).ToList();
        if (loadable.Count != enabled.Count)
        {
            // The cells hold only what will load; blocked plugins' assemblies are left out of every plan.
            classifications = Classify(loadable, assignment, manifests);
        }

        cancellationToken.ThrowIfCancellationRequested();
        AnalyzeWpfDuplicates(classifications, issues);
        AnalyzeNativeCollisions(classifications, issues);

        var planned = all.Values.Select(p => !p.Enabled
            ? new PlannedPlugin(p, null, null, PluginStatus.Disabled, "The plugin is disabled.")
            : new PlannedPlugin(p, manifests[p.Id], assignment.Cells[p.Id], PluginStatus.NotLoaded, blocked.GetValueOrDefault(p.Id))
            {
                DiscoveredEntryPoint = discovered.GetValueOrDefault(p.Id),
            })
            .ToList();
        return Finish(issues, planned, classifications, TopologicalOrder(planned.Where(p => p.IsLoadable).ToList()));
    }

    private LoadPlan Finish(IssueList issues, IReadOnlyList<PlannedPlugin> plugins, IReadOnlyList<CellClassification> cells, IReadOnlyList<PlannedPlugin> order)
    {
        var report = new ValidationReport(issues.ToList());
        foreach (var issue in report.Issues)
        {
            if (issue.Severity == ValidationSeverity.Error)
            {
                ValidationLog.Error(_logger, issue);
            }
            else
            {
                ValidationLog.Warning(_logger, issue);
            }
        }

        ValidationLog.Summary(_logger, plugins.Count, report.Errors.Count(), report.Warnings.Count(), order.Count);
        return new LoadPlan(report, plugins, cells, order);
    }

    /// <summary>The plugin's package and its cell-private packages against the disk, and its manifest. Returns the manifest.</summary>
    private PluginManifest? CheckPackages(PluginState plugin, IssueList issues)
    {
        if (plugin.ManifestSchemaVersion > PluginManifest.CurrentSchemaVersion)
        {
            issues.Add(ValidationSeverity.Error, ValidationCodes.ManifestSchemaUnsupported,
                $"{plugin.Id} {plugin.Version} has manifest schema version {plugin.ManifestSchemaVersion}; this version of Alcove supports up to {PluginManifest.CurrentSchemaVersion}.", plugin.Id);
        }

        foreach (var dependency in plugin.Dependencies.Where(d => d.Classification == DependencyClass.CellPrivate))
        {
            CheckPackage(plugin.Id, dependency.Id, dependency.Version.ToNormalizedString(), _layout.GetPackageDirectory(dependency.Id, dependency.Version), dependency.Files, issues);
        }

        if (!CheckPackage(plugin.Id, plugin.Id, plugin.Version.ToNormalizedString(), _layout.GetPackageDirectory(plugin.Id, plugin.Version), plugin.Files, issues))
        {
            return null;
        }

        var manifestPath = _layout.GetManifestPath(plugin.Id, plugin.Version);
        PluginManifest manifest;
        try
        {
            using var stream = File.OpenRead(manifestPath);
            manifest = PluginManifest.Read(stream);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            issues.Add(ValidationSeverity.Error, ValidationCodes.ManifestMissing,
                $"The plugin package {plugin.Id} {plugin.Version} has no manifest ('{manifestPath}').", plugin.Id);
            return null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            issues.Add(ValidationSeverity.Error, ValidationCodes.ManifestInvalid,
                $"The manifest of {plugin.Id} {plugin.Version} can't be read: {ex.Message}", plugin.Id);
            return null;
        }

        if (manifest.SchemaVersion is < 1 or > PluginManifest.CurrentSchemaVersion)
        {
            issues.Add(ValidationSeverity.Error, ValidationCodes.ManifestSchemaUnsupported,
                $"The manifest of {plugin.Id} {plugin.Version} has schema version {manifest.SchemaVersion}; this version of Alcove supports 1 to {PluginManifest.CurrentSchemaVersion}.", plugin.Id);
        }

        return manifest;
    }

    /// <summary>
    /// The compatibility gate (ADR 0002): every range the plugin and its cell-private dependencies declare on a
    /// host-owned or host-shared package must be satisfied by the version the running host has.
    /// </summary>
    private void CheckHostRanges(PluginState plugin, IssueList issues)
    {
        foreach (var dependency in plugin.Dependencies.Where(d => d.Classification is DependencyClass.HostOwned or DependencyClass.HostShared))
        {
            if (_host.IsFrameworkProvided(dependency.Id, dependency.Version))
            {
                continue;   // skipped, not version-checked (the SDK prunes these)
            }

            var hostVersion = _host.FindPackage(dependency.Id)?.Version;
            var hostOwned = _host.IsHostOwnedPackage(dependency.Id, dependency.Version);
            if (hostOwned && hostVersion is null)
            {
                continue;   // HostOwnedMissing, from classification
            }

            foreach (var request in dependency.RequestedBy.Where(r => hostVersion is null || !r.Range.Satisfies(hostVersion)))
            {
                var declared = string.Equals(request.PackageId, plugin.Id, StringComparison.OrdinalIgnoreCase)
                    ? ""
                    : $" (declared by its dependency {request.PackageId})";
                var range = request.Range.ToNormalizedString();
                if (hostOwned)
                {
                    issues.Add(ValidationSeverity.Error, ValidationCodes.HostOwnedConflict,
                        $"{plugin.Id} needs the host-owned package {dependency.Id} {range}{declared}, but the host has {hostVersion}: the plugin is incompatible with this host.",
                        plugin.Id);
                }
                else
                {
                    issues.Add(ValidationSeverity.Error, ValidationCodes.HostSharedConflict,
                        $"{plugin.Id} needs {dependency.Id} {range}{declared}, which was resolved from the host, but the host now has {(hostVersion is null ? "no such package" : hostVersion.ToString())}. The plugin must be resolved again: reinstall it.",
                        plugin.Id);
                }
            }
        }
    }

    /// <summary>Checks a package folder and its recorded files (size and timestamp). Returns whether the folder exists.</summary>
    private static bool CheckPackage(string pluginId, string packageId, string version, string directory, IReadOnlyList<FileRecord> files, IssueList issues)
    {
        if (!Directory.Exists(directory))
        {
            issues.Add(ValidationSeverity.Error, ValidationCodes.PackageMissing,
                $"{(string.Equals(pluginId, packageId, StringComparison.OrdinalIgnoreCase) ? "The plugin package" : $"{pluginId} needs the package")} {packageId} {version}, which isn't in the plugin root ('{directory}').",
                pluginId);
            return false;
        }

        var missing = new List<string>();
        var changed = new List<string>();
        foreach (var file in files)
        {
            var info = new FileInfo(Path.Combine(directory, file.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (!info.Exists)
            {
                missing.Add(file.Path);
            }
            else if (info.Length != file.Size || info.LastWriteTimeUtc != file.LastWriteTimeUtc)
            {
                changed.Add(file.Path);
            }
        }

        if (missing.Count > 0)
        {
            issues.Add(ValidationSeverity.Error, ValidationCodes.FileMissing,
                $"{Count(missing)} of {packageId} {version} {(missing.Count == 1 ? "is" : "are")} missing from the plugin root: {List(missing)}.", pluginId);
        }

        if (changed.Count > 0)
        {
            issues.Add(ValidationSeverity.Error, ValidationCodes.FileChanged,
                $"{Count(changed)} of {packageId} {version} changed since install (size or timestamp): {List(changed)}.", pluginId);
        }

        return true;

        static string Count(List<string> paths) => paths.Count == 1 ? "1 file" : $"{paths.Count} files";

        static string List(List<string> paths) =>
            string.Join(", ", paths.Take(MaxFilesListed)) + (paths.Count > MaxFilesListed ? ", ..." : "");
    }

    private static void CheckDependencyGraph(IReadOnlyList<PluginState> enabled, Dictionary<string, PluginState> all, IssueList issues)
    {
        foreach (var plugin in enabled)
        {
            foreach (var dependency in plugin.Dependencies.Where(d => d.Classification == DependencyClass.Plugin))
            {
                if (!all.TryGetValue(dependency.Id, out var target))
                {
                    issues.Add(ValidationSeverity.Error, ValidationCodes.PluginDependencyMissing,
                        $"{plugin.Id} depends on the plugin {dependency.Id} {dependency.Version}, which isn't installed.", plugin.Id);
                }
                else if (!target.Enabled)
                {
                    issues.Add(ValidationSeverity.Error, ValidationCodes.PluginDependencyDisabled,
                        $"{plugin.Id} depends on the plugin {dependency.Id}, which is disabled.", plugin.Id);
                }
                else
                {
                    // The installed version can change on its own (an update of the dependency), so check every declared range.
                    foreach (var request in dependency.RequestedBy.Where(r => !r.Range.Satisfies(target.Version)))
                    {
                        var declared = string.Equals(request.PackageId, plugin.Id, StringComparison.OrdinalIgnoreCase)
                            ? ""
                            : $" (declared by its dependency {request.PackageId})";
                        issues.Add(ValidationSeverity.Error, ValidationCodes.PluginDependencyConflict,
                            $"{plugin.Id} needs the plugin {dependency.Id} {request.Range.ToNormalizedString()}{declared}, but version {target.Version} is installed.",
                            plugin.Id);
                    }
                }
            }
        }

        foreach (var cycle in FindCycles(enabled))
        {
            var path = string.Join(" -> ", cycle.Append(cycle[0]));
            foreach (var member in cycle)
            {
                issues.Add(ValidationSeverity.Error, ValidationCodes.PluginDependencyCycle,
                    $"{member} is part of a plugin dependency cycle: {path}.", member);
            }
        }
    }

    /// <summary>Strongly connected components with more than one plugin, or a plugin depending on itself (Tarjan).</summary>
    private static List<List<string>> FindCycles(IReadOnlyList<PluginState> plugins)
    {
        var graph = plugins.ToDictionary(
            p => p.Id,
            p => PluginDependencies(p).Where(d => plugins.Any(q => string.Equals(q.Id, d, StringComparison.OrdinalIgnoreCase))).ToList(),
            StringComparer.OrdinalIgnoreCase);
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var low = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var onStack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>();
        var cycles = new List<List<string>>();
        var next = 0;

        foreach (var id in graph.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            if (!index.ContainsKey(id))
            {
                Visit(id);
            }
        }

        return cycles;

        void Visit(string id)
        {
            index[id] = low[id] = next++;
            stack.Push(id);
            onStack.Add(id);
            foreach (var dependency in graph[id])
            {
                if (!index.ContainsKey(dependency))
                {
                    Visit(dependency);
                    low[id] = Math.Min(low[id], low[dependency]);
                }
                else if (onStack.Contains(dependency))
                {
                    low[id] = Math.Min(low[id], index[dependency]);
                }
            }

            if (low[id] != index[id])
            {
                return;
            }

            var component = new List<string>();
            string member;
            do
            {
                member = stack.Pop();
                onStack.Remove(member);
                component.Add(member);
            }
            while (!string.Equals(member, id, StringComparison.OrdinalIgnoreCase));

            if (component.Count > 1 || graph[id].Contains(id, StringComparer.OrdinalIgnoreCase))
            {
                component.Reverse();
                cycles.Add(component);
            }
        }
    }

    /// <summary>
    /// Entry points of plugins whose manifest names none: a plugin's one public, non-abstract class implementing
    /// <see cref="IPlugin"/> (read from metadata; base types followed into its cell's assemblies, provider cells' and
    /// the host's, as for <see cref="ICellServicesFactory.ServicePluginInterfaces"/>). Several are an error
    /// (<see cref="ValidationCodes.EntryPointAmbiguous"/>); none means the plugin is scanned for
    /// <see cref="ExtensionAttribute"/> types.
    /// </summary>
    private Dictionary<string, string> DiscoverEntryPoints(
        IEnumerable<PluginState> plugins, Dictionary<string, PluginManifest?> manifests, CellAssignment assignment, IReadOnlyList<CellClassification> cells, IssueList issues)
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var plans = cells.ToDictionary(c => c.Cell, c => c.Plan, StringComparer.Ordinal);
        foreach (var plugin in plugins)
        {
            if (manifests.GetValueOrDefault(plugin.Id) is not { EntryPoint: null }
                || !assignment.Cells.TryGetValue(plugin.Id, out var cell)
                || !plans.TryGetValue(cell, out var plan))
            {
                continue;
            }

            var own = plan.Private.Values
                .Where(a => string.Equals(a.PackageId, plugin.Id, StringComparison.OrdinalIgnoreCase))
                .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .Select(a => a.Path)
                .ToList();
            IReadOnlyList<string> candidates;
            try
            {
                candidates = EntryPointInterfaceDetector.FindImplementations(own, typeof(IPlugin).FullName!, Resolve);
            }
            catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException)
            {
                ValidationLog.InspectFailed(_logger, string.Join(", ", own), ex.Message);
                continue;   // activation reports unreadable assemblies
            }

            if (candidates.Count == 1)
            {
                found[plugin.Id] = candidates[0];
            }
            else if (candidates.Count > 1)
            {
                issues.Add(ValidationSeverity.Error, ValidationCodes.EntryPointAmbiguous,
                    $"{plugin.Id} {plugin.Version} names no entry point, and its assemblies have {candidates.Count} public classes implementing IPlugin ({string.Join(", ", candidates)}). Set PluginEntryPoint in its project to the one to use.",
                    plugin.Id, cell);
            }

            // As the cell answers: its private assemblies, a provider cell's, else the host's (not the shared framework's).
            string? Resolve(string name) =>
                plan.Private.TryGetValue(name, out var asset) ? asset.Path
                : plan.Provided.TryGetValue(name, out var provider)
                    ? (plans.TryGetValue(provider, out var providerPlan) && providerPlan.Private.TryGetValue(name, out var provided) ? provided.Path : null)
                : _hostFiles.Value.Find(name, isHost: plan.HostOwned.Contains(name) || plan.HostShared.Contains(name));
        }

        return found;
    }

    private List<CellClassification> Classify(IEnumerable<PluginState> plugins, CellAssignment assignment, Dictionary<string, PluginManifest?> manifests)
    {
        var state = new StateDocument { Plugins = plugins.Select(p => p with { Cell = assignment.Cells[p.Id] }).ToList() };
        return new AssemblyClassifier(_host, _layout, _assets, p => manifests.GetValueOrDefault(p.Id)).Classify(state).ToList();
    }

    private static void AddClassificationIssue(ClassificationIssue issue, IssueList issues)
    {
        var (severity, code) = issue.Kind switch
        {
            ClassificationIssueKind.HostOwnedConflict => (ValidationSeverity.Error, ValidationCodes.HostOwnedConflict),
            ClassificationIssueKind.HostOwnedMissing => (ValidationSeverity.Error, ValidationCodes.HostOwnedMissing),
            ClassificationIssueKind.HostSharedDowngraded => (ValidationSeverity.Warning, ValidationCodes.HostSharedDowngraded),
            ClassificationIssueKind.CellVersionConflict => (ValidationSeverity.Warning, ValidationCodes.CellVersionConflict),
            ClassificationIssueKind.ExposedDependencyConflict => (ValidationSeverity.Error, ValidationCodes.ExposedDependencyConflict),

            // PackageMissing and PluginDependencyMissing: checked per plugin before classification.
            _ => (ValidationSeverity.Warning, (string?)null),
        };

        if (code is not null)
        {
            issues.Add(severity, code, issue.Message, issue.PluginId, issue.Cell);
        }
    }

    /// <summary>
    /// WPF-relevant names in more than one load context (architecture §4): identical private copies are shared
    /// across their cells (ADR 0015), anything else is a "will break" or "may break" warning.
    /// </summary>
    private void AnalyzeWpfDuplicates(List<CellClassification> cells, IssueList issues)
    {
        // Cheap first pass: only names that more than one load context will hold can be duplicates.
        var contexts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in cells.SelectMany(c => c.Plan.Private.Keys))
        {
            contexts[name] = contexts.GetValueOrDefault(name) + 1;
        }

        var hostCopies = contexts.Keys.Where(n => _host.Assemblies.ContainsKey(n)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var duplicated = contexts.Where(c => c.Value + (hostCopies.Contains(c.Key) ? 1 : 0) > 1).Select(c => c.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (duplicated.Count == 0)
        {
            return;
        }

        // Relevance depends on the other assemblies of the same context, so every private assembly of an involved cell is read.
        var planned = new List<PlannedAssembly>();
        foreach (var cell in cells.Where(c => c.Plan.Private.Keys.Any(duplicated.Contains)))
        {
            foreach (var asset in cell.Plan.Private.Values)
            {
                if (Inspect(asset.Path) is { } info)
                {
                    planned.Add(new PlannedAssembly(cell.Cell, info, asset.PackageId, asset.PackageVersion));
                }
            }
        }

        foreach (var name in duplicated.Where(hostCopies.Contains))
        {
            if (_hostAssemblyPath(name) is { } path && Inspect(path) is { } info)
            {
                var owner = _host.Assemblies[name].Owner;
                planned.Add(new PlannedAssembly(null, info, owner, _host.FindPackage(owner)?.Version));
            }
        }

        foreach (var finding in WpfDuplicateDetector.Detect(planned))
        {
            if (finding.Kind == WpfDuplicateKind.ShareAcrossCells)
            {
                foreach (var copy in finding.Copies.Where(c => c.Cell is not null))
                {
                    var plan = cells.Single(c => c.Cell == copy.Cell).Plan;
                    if (plan.Private.TryGetValue(finding.AssemblyName, out var asset))
                    {
                        plan.Private[finding.AssemblyName] = asset with { WpfRelevant = true };
                    }
                }

                continue;
            }

            var (code, effect) = finding.Kind == WpfDuplicateKind.WillBreak
                ? (ValidationCodes.WpfDuplicateWillBreak, "WPF will resolve some of its XAML to the copy loaded last, so views of the others break")
                : (ValidationCodes.WpfDuplicateMayBreak, "name-only lookups (unversioned pack URIs, loose XAML) go to the copy loaded last");
            var copies = string.Join("; ", finding.Copies.Select(c => $"{c.Assembly.Identity.Version} in {(c.Cell is null ? "the host" : $"cell '{c.Cell}'")}"));
            foreach (var cell in finding.Copies.Select(c => c.Cell).OfType<string>().Distinct(StringComparer.Ordinal))
            {
                issues.Add(ValidationSeverity.Warning, code,
                    $"The WPF-relevant assembly {finding.AssemblyName} is loaded more than once ({copies}): {effect} ({finding.Reason}). List its package in HostOwnedPackages, or align the versions.",
                    cell: cell);
            }
        }
    }

    private WpfAssemblyInfo? Inspect(string path)
    {
        try
        {
            return WpfAssemblyInspector.Inspect(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            ValidationLog.InspectFailed(_logger, path, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Different native files with one file name in several cells: a native library's own dependencies are
    /// matched by file name across the process, so the cells may end up sharing one (architecture §5).
    /// </summary>
    private static void AnalyzeNativeCollisions(List<CellClassification> cells, IssueList issues)
    {
        var byFileName = cells
            .SelectMany(c => c.Plan.NativeLibraries.Values.Select(n => (Cell: c.Cell, Native: n)))
            .GroupBy(x => x.Native.FileName, CellPlan.NativeFileNameComparer);
        foreach (var group in byFileName)
        {
            if (group.Select(x => x.Native.Path).Distinct(StringComparer.Ordinal).Count() < 2)
            {
                continue;
            }

            var copies = string.Join("; ", group.Select(x => $"{x.Native.PackageId} {x.Native.PackageVersion} in cell '{x.Cell}'"));
            foreach (var (cell, _) in group)
            {
                issues.Add(ValidationSeverity.Warning, ValidationCodes.NativeLibraryCollision,
                    $"The native library {group.Key} comes from different files in several cells ({copies}). Native libraries' own dependencies are matched by file name across the whole process, so the cells may end up sharing one.",
                    cell: cell);
            }
        }
    }

    /// <summary>Dependencies first; ties by plugin ID, so the order is deterministic.</summary>
    private static List<PlannedPlugin> TopologicalOrder(List<PlannedPlugin> plugins)
    {
        var byId = plugins.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
        var remaining = plugins.ToDictionary(
            p => p.Id,
            p => p.PluginDependencies.Where(byId.ContainsKey).ToHashSet(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
        var ready = new SortedSet<string>(remaining.Where(r => r.Value.Count == 0).Select(r => r.Key), StringComparer.OrdinalIgnoreCase);
        var order = new List<PlannedPlugin>(plugins.Count);
        while (ready.Count > 0)
        {
            var id = ready.Min!;
            ready.Remove(id);
            remaining.Remove(id);
            order.Add(byId[id]);
            foreach (var (other, dependencies) in remaining)
            {
                if (dependencies.Remove(id) && dependencies.Count == 0)
                {
                    ready.Add(other);
                }
            }
        }

        // Cycles were blocked before; anything left would be a bug.
        if (remaining.Count > 0)
        {
            throw new InvalidOperationException($"Plugin dependency cycle among loadable plugins: {string.Join(", ", remaining.Keys)}.");
        }

        return order;
    }

    private static IEnumerable<string> PluginDependencies(PluginState plugin) =>
        plugin.Dependencies.Where(d => d.Classification == DependencyClass.Plugin).Select(d => d.Id);

    /// <summary>The issues found so far, without duplicates.</summary>
    private sealed class IssueList
    {
        private readonly List<ValidationIssue> _issues = [];
        private readonly HashSet<ValidationIssue> _seen = [];

        public void Add(ValidationSeverity severity, string code, string message, string? pluginId = null, string? cell = null)
        {
            var issue = new ValidationIssue(severity, code, message, pluginId, cell);
            if (_seen.Add(issue))
            {
                _issues.Add(issue);
            }
        }

        /// <summary>Plugin ID → the first error that blocks it.</summary>
        public Dictionary<string, string> BlockedPlugins()
        {
            var blocked = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var issue in _issues.Where(i => i.Severity == ValidationSeverity.Error && i.PluginId is not null))
            {
                blocked.TryAdd(issue.PluginId!, issue.Message);
            }

            return blocked;
        }

        public List<ValidationIssue> ToList() => _issues.ToList();
    }
}

/// <summary>The files of the default context's assemblies (<c>TRUSTED_PLATFORM_ASSEMBLIES</c>), by simple name.</summary>
internal static class TrustedPlatformAssemblies
{
    /// <summary>The path of the host assembly with this simple name, or <see langword="null"/>.</summary>
    public static string? Find(string simpleName)
    {
        var list = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (string.IsNullOrEmpty(list))
        {
            return null;
        }

        foreach (var path in list.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(Path.GetFileNameWithoutExtension(path), simpleName, StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }
        }

        return null;
    }
}

using Alcove.Internal.Feeds;
using Microsoft.Extensions.Logging;
using NuGet.Configuration;
using NuGet.DependencyResolver;
using NuGet.Frameworks;
using NuGet.LibraryModel;
using NuGet.Packaging.Core;
using NuGet.RuntimeModel;
using NuGet.Versioning;

namespace Alcove.Internal.Resolution;

/// <summary>What to resolve for one cell.</summary>
/// <param name="Cell">The cell's name (for messages).</param>
/// <param name="Plugins">The cell's plugins at their exact versions (existing ones and new ones).</param>
/// <param name="HostPins">The host's packages at the host's versions (minus the cell's private dependencies).</param>
/// <param name="PluginPins">Plugins outside the cell at their versions; they are leaves here.</param>
/// <param name="IsHostOwned">Whether a pinned host package is host-owned (a downgrade makes the plugin incompatible).</param>
internal sealed record CellResolutionRequest(
    string Cell,
    IReadOnlyList<PackageIdentity> Plugins,
    IReadOnlyDictionary<string, NuGetVersion> HostPins,
    IReadOnlyDictionary<string, NuGetVersion> PluginPins,
    Func<string, bool> IsHostOwned);

/// <summary>A package the walk accepted: one version per ID, and where it came from.</summary>
/// <param name="Id">The package ID as requested.</param>
/// <param name="Version">The version.</param>
/// <param name="Provider">The source: a feed, the plugin root, or the pins.</param>
internal sealed record ResolvedPackage(string Id, NuGetVersion Version, IRemoteDependencyProvider? Provider);

/// <summary>The result of resolving one cell.</summary>
internal sealed class CellResolution
{
    /// <summary>The accepted packages by ID (the plugins, their dependencies, the pins), without the synthetic root.</summary>
    public required IReadOnlyDictionary<string, ResolvedPackage> Packages { get; init; }

    /// <summary>Host packages still pinned after the walks (resolved to the host's version).</summary>
    public required IReadOnlySet<string> HostPinned { get; init; }

    /// <summary>Host-shared packages that were unpinned because a plugin needs a higher version: cell-private now.</summary>
    public required IReadOnlySet<string> Unpinned { get; init; }

    /// <summary>Host-owned conflicts: the plugin is incompatible with this host.</summary>
    public required IReadOnlyList<string> Incompatibilities { get; init; }

    /// <summary>Other errors: range conflicts, downgrades, cycles, packages not found.</summary>
    public required IReadOnlyList<string> Errors { get; init; }

    /// <summary>How many walks it took (one, plus one per round of unpinning).</summary>
    public required int Walks { get; init; }
}

/// <summary>
/// Resolves one cell with NuGet's <see cref="RemoteDependencyWalker"/> (PackageReference semantics: nearest wins,
/// lowest applicable version) from a synthetic root that depends on all the cell's plugins and pins every host
/// package at the host's version (ADR 0016, architecture §5). A downgrade of a pinned host package means a plugin
/// needs more than the host has: host-owned → incompatible; host-shared → unpin it and walk again, so it becomes
/// cell-private. Range conflicts come from <c>Analyze().VersionConflicts</c>.
/// </summary>
internal sealed class CellResolver(FeedSession session, IReadOnlyList<IRemoteDependencyProvider> sources, NuGetFramework framework, ILogger logger)
{
    private const string RootName = "__alcove_cell__";

    public async Task<CellResolution> ResolveAsync(CellResolutionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var unpinned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var incompatible = new List<string>();
        var errors = new List<string>();
        var walks = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            walks++;
            FeedLog.Walking(logger, request.Cell, string.Join(", ", request.Plugins), walks);
            var hostPins = request.HostPins.Where(p => !unpinned.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
            var pins = new Dictionary<string, NuGetVersion>(hostPins, StringComparer.OrdinalIgnoreCase);
            foreach (var (id, version) in request.PluginPins)
            {
                pins[id] = version;
            }

            var graph = await WalkAsync(request.Plugins, pins).ConfigureAwait(false);
            var analysis = graph.Analyze();
            errors.Clear();

            var newlyUnpinned = false;
            foreach (var downgrade in analysis.Downgrades)
            {
                var id = downgrade.DowngradedFrom.Key.Name;
                var wanted = downgrade.DowngradedFrom.Key.VersionRange ?? VersionRange.All;
                var got = downgrade.DowngradedTo.Item?.Key.Version;
                var neededBy = Describe(downgrade.DowngradedFrom.OuterNode);
                if (hostPins.ContainsKey(id))
                {
                    if (request.IsHostOwned(id))
                    {
                        Add(incompatible, $"{neededBy} needs {id} {wanted.PrettyPrint()}, but the host has {got}; {id} is host-owned, so the plugin is incompatible with this host.");
                    }
                    else if (unpinned.Add(id))
                    {
                        newlyUnpinned = true;
                        FeedLog.HostSharedUnpinned(logger, request.Cell, id, got!, wanted.PrettyPrint(), neededBy);
                    }
                }
                else if (request.PluginPins.ContainsKey(id))
                {
                    Add(errors, $"{neededBy} needs the plugin {id} {wanted.PrettyPrint()}, but {id} {got} is installed (it is in another cell and isn't changed by this install).");
                }
                else
                {
                    Add(errors, $"{neededBy} needs {id} {wanted.PrettyPrint()}, but {id} {got} was chosen for cell '{request.Cell}' (a nearer dependency wins); a cell holds one version of each package.");
                }
            }

            if (newlyUnpinned)
            {
                continue;   // walk again, so the unpinned packages resolve from the feeds
            }

            foreach (var conflict in analysis.VersionConflicts)
            {
                var (selected, conflicting) = (conflict.Selected, conflict.Conflicting);
                Add(errors,
                    $"Version conflict in cell '{request.Cell}': {Describe(conflicting.OuterNode)}{Via(conflicting.OuterNode)} needs {conflicting.Key.Name} {conflicting.Key.VersionRange?.PrettyPrint()}, " +
                    $"but {Describe(selected.OuterNode)}{Via(selected.OuterNode)} needs {selected.Key.Name} {selected.Key.VersionRange?.PrettyPrint()}, " +
                    $"and {selected.Key.Name} {selected.Item?.Key.Version} was selected; a cell holds one version of each package.");
            }

            foreach (var cycle in analysis.Cycles)
            {
                Add(errors, $"Dependency cycle in cell '{request.Cell}': {Path(cycle)}.");
            }

            var accepted = new Dictionary<string, ResolvedPackage>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in Accepted(graph))
            {
                if (node.Item.Key.Type == LibraryType.Unresolved)
                {
                    Add(errors, $"{node.Key.Name} {node.Key.VersionRange?.PrettyPrint()} (needed by {Describe(node.OuterNode)}) wasn't found on {FeedList()}.");
                    continue;
                }

                accepted.TryAdd(node.Key.Name, new ResolvedPackage(node.Key.Name, node.Item.Key.Version, node.Item.Data.Match?.Provider));
            }

            return new CellResolution
            {
                Packages = accepted,
                HostPinned = hostPins.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase),
                Unpinned = unpinned,
                Incompatibilities = incompatible,
                Errors = errors.ToList(),
                Walks = walks,
            };
        }
    }

    private async Task<GraphNode<RemoteResolveResult>> WalkAsync(IReadOnlyList<PackageIdentity> plugins, IReadOnlyDictionary<string, NuGetVersion> pins)
    {
        var dependencies = plugins
            .Select(p => Exact(p.Id, p.Version))
            .Concat(pins.Where(p => !plugins.Any(q => string.Equals(q.Id, p.Key, StringComparison.OrdinalIgnoreCase))).Select(p => Exact(p.Key, p.Value)))
            .ToList();

        var context = new RemoteWalkContext(session.Cache, PackageSourceMapping.GetPackageSourceMapping(NullSettings.Instance), session.NuGetLogger);
        context.ProjectLibraryProviders.Add(new RootProvider(RootName, dependencies));
        context.LocalLibraryProviders.Add(new PinnedDependencyProvider(pins));
        foreach (var source in sources)
        {
            context.RemoteLibraryProviders.Add(source);
        }

        var walker = new RemoteDependencyWalker(context);
        return await walker.WalkAsync(
            new LibraryRange(RootName, VersionRange.Parse("1.0.0"), LibraryDependencyTarget.Project),
            framework,
            runtimeIdentifier: null!,
            runtimeGraph: RuntimeGraph.Empty,
            recursive: true).ConfigureAwait(false);

        static LibraryDependency Exact(string id, NuGetVersion version) => new()
        {
            LibraryRange = new LibraryRange(id, new VersionRange(version, true, version, true), LibraryDependencyTarget.Package),
        };
    }

    /// <summary>The accepted nodes (the flattened result) below the root.</summary>
    private static IEnumerable<GraphNode<RemoteResolveResult>> Accepted(GraphNode<RemoteResolveResult> root)
    {
        var pending = new Stack<GraphNode<RemoteResolveResult>>();
        pending.Push(root);
        while (pending.TryPop(out var node))
        {
            if (node.OuterNode is not null && node.Disposition == Disposition.Accepted && node.Item is not null)
            {
                yield return node;
            }

            foreach (var inner in node.InnerNodes)
            {
                pending.Push(inner);
            }
        }
    }

    private string FeedList() =>
        session.Repositories.Count == 0 ? "any feed (no feed is configured or allowed)" : $"the feeds ({string.Join(", ", session.Repositories.Select(r => r.PackageSource.Source))})";

    private static string Describe(GraphNode<RemoteResolveResult>? node) =>
        node?.Item?.Key is { } key && !string.Equals(key.Name, RootName, StringComparison.Ordinal)
            ? $"{key.Name} {key.Version}"
            : "the install";

    /// <summary>" (via A 1.0.0 -> B 2.0.0)" for a node deeper than the cell's plugins, else nothing.</summary>
    private static string Via(GraphNode<RemoteResolveResult>? node)
    {
        var path = Path(node);
        return path.Contains(" -> ", StringComparison.Ordinal) ? $" (via {path})" : "";
    }

    private static string Path(GraphNode<RemoteResolveResult>? node)
    {
        var parts = new List<string>();
        for (var current = node; current is not null && !string.Equals(current.Key.Name, RootName, StringComparison.Ordinal); current = current.OuterNode)
        {
            parts.Add(current.Item?.Key.Version is { } version ? $"{current.Key.Name} {version}" : $"{current.Key.Name} {current.Key.VersionRange?.PrettyPrint()}");
        }

        parts.Reverse();
        return string.Join(" -> ", parts);
    }

    private static void Add(List<string> messages, string message)
    {
        if (!messages.Contains(message, StringComparer.Ordinal))
        {
            messages.Add(message);
        }
    }
}

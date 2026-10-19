using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.DependencyResolver;
using NuGet.Frameworks;
using NuGet.LibraryModel;
using NuGet.Protocol.Core.Types;
using NuGet.RuntimeModel;
using NuGet.Versioning;

namespace Spike;

public enum DepClass { Plugin, HostOwnedProvided, HostSharedProvided, FrameworkPruned, PrivateConflict, PrivateMissing, PrivateMarked, IncompatibleHostOwned, Unresolved }

public sealed record Classified(string Id, NuGetVersion? Version, DepClass Class, string Source, List<string> Requests, string? Note)
{
    public List<VersionRange> Ranges { get; init; } = new();
}

public sealed record ResolveOutcome(List<Classified> Packages, AnalyzeResult<RemoteResolveResult> Analysis, List<string> Pruned, int Walks, List<string> Log);

/// <summary>
/// Resolution of one compartment's plugins against the host, using NuGet.DependencyResolver.Core's
/// RemoteDependencyWalker (PackageReference semantics: lowest applicable version, nearest wins, direct dependency wins).
///
/// Host packages are pinned as direct dependencies of the synthetic root, so the host's version wins wherever it
/// satisfies the plugins ("nearest wins"). If a plugin needs more than the host has, the walker reports a
/// *downgrade*: host-owned → incompatible; host-shared → unpin it and re-walk, making that package compartment-private.
/// </summary>
public sealed class CompartmentResolver(HostInventory host, ISet<string> hostOwned, IReadOnlyList<SourceRepository> repos, NuGetFramework framework, ILogger log)
{
    const string RootName = "__compartment__";
    // SPIKE_NOCACHE=1: no user-wide http-cache; NuGet writes responses to GeneratedTempFolder instead.
    readonly SourceCacheContext _cache = Environment.GetEnvironmentVariable("SPIKE_NOCACHE") == "1"
        ? new() { NoCache = true, GeneratedTempFolder = Path.Combine(Feeds.WorkDir, "tmp-http") }
        : new();

    public ISet<string> PrivateDependencies { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);


    /// <summary>Pruning rule (approximates the SDK's PrunePackageReference / PackageOverrides.txt at runtime, where no targeting pack exists).</summary>
    public bool IsFrameworkProvided(string id, VersionRange range) => IsFrameworkProvided(host, id, range);

    public static bool IsFrameworkProvided(HostInventory host, string id, VersionRange? range)
    {
        var fxMajor = host.Frameworks.Count == 0 ? 0 : NuGetVersion.Parse(host.Frameworks[0].Version).Major;
        if (range?.MinVersion is { } min && min.Major > fxMajor) return false;
        if (host.FrameworkAssemblies.ContainsKey(id)) return true;
        // old RID-split implementation packages, e.g. runtime.any.System.Collections, runtime.unix.System.IO.FileSystem
        return id.StartsWith("runtime.", StringComparison.OrdinalIgnoreCase)
            && host.FrameworkAssemblies.Keys.Any(a => id.EndsWith("." + a, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<ResolveOutcome> ResolveAsync(IReadOnlyList<(string Id, VersionRange Range)> plugins, bool pinHost = true, CancellationToken ct = default)
    {
        var unpinned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var incompatible = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var notes = new List<string>();
        var pluginIds = plugins.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int walks = 0;
        while (true)
        {
            walks++;
            var pins = !pinHost ? new Dictionary<string, NuGetVersion>() : host.Packages.Values.Concat(host.Projects.Values)
                .Where(p => p.Type != "project" || p.Assemblies.Count > 0)
                .Where(p => !unpinned.Contains(p.Id) && !pluginIds.Contains(p.Id) && !PrivateDependencies.Contains(p.Id))
                .Where(p => p.Id != Path.GetFileNameWithoutExtension(host.AppDepsFile).Replace(".deps", ""))
                .ToDictionary(p => p.Id, p => NuGetVersion.Parse(p.Version), StringComparer.OrdinalIgnoreCase);

            var (graph, feeds) = await WalkAsync(plugins, pins, unpinned, ct);
            var analysis = graph.Analyze();

            var newlyUnpinned = false;
            foreach (var d in analysis.Downgrades)
            {
                var id = d.DowngradedFrom.Key.Name;
                var wanted = d.DowngradedFrom.Key.VersionRange;
                var got = d.DowngradedTo.Item?.Key.Version;
                var by = d.DowngradedFrom.OuterNode?.Item?.Key.ToString() ?? "?";
                if (!pins.ContainsKey(id)) continue;   // a downgrade between plugin packages: report, don't touch
                if (hostOwned.Contains(id))
                    incompatible[id] = $"{by} needs {id} {wanted}, host-owned version is {got}";
                else if (unpinned.Add(id))
                {
                    newlyUnpinned = true;
                    notes.Add($"walk {walks}: host-shared {id} {got} doesn't satisfy {by} -> {id} {wanted}; unpinning, will be compartment-private");
                }
            }
            if (newlyUnpinned) continue;   // re-walk so unpinned packages resolve from the feeds

            return new ResolveOutcome(Classify(graph, pluginIds, unpinned, incompatible, pinHost), analysis,
                feeds.SelectMany(f => f.Pruned).Distinct().ToList(), walks, notes);
        }
    }

    async Task<(GraphNode<RemoteResolveResult>, List<FeedDependencyProvider>)> WalkAsync(
        IReadOnlyList<(string Id, VersionRange Range)> plugins, IReadOnlyDictionary<string, NuGetVersion> pins, ISet<string> unpinned, CancellationToken ct)
    {
        var deps = plugins.Select(p => new LibraryDependency { LibraryRange = new LibraryRange(p.Id, p.Range, LibraryDependencyTarget.Package) })
            .Concat(pins.Select(p => new LibraryDependency { LibraryRange = new LibraryRange(p.Key, new VersionRange(p.Value, true, p.Value, true), LibraryDependencyTarget.Package) }))
            .ToList();

        var ctx = new RemoteWalkContext(_cache, PackageSourceMapping.GetPackageSourceMapping(NullSettings.Instance), log);
        ctx.ProjectLibraryProviders.Add(new RootProvider(RootName, deps));
        if (pins.Count > 0) ctx.LocalLibraryProviders.Add(new HostDependencyProvider(host, unpinned));
        var feeds = repos.Select(r => new FeedDependencyProvider(r, IsFrameworkProvided)).ToList();
        foreach (var f in feeds) ctx.RemoteLibraryProviders.Add(f);

        var walker = new RemoteDependencyWalker(ctx);
        var graph = await walker.WalkAsync(new LibraryRange(RootName, VersionRange.Parse("1.0.0"), LibraryDependencyTarget.Project),
            framework, runtimeIdentifier: null, runtimeGraph: RuntimeGraph.Empty, recursive: true);
        return (graph, feeds);
    }

    List<Classified> Classify(GraphNode<RemoteResolveResult> graph, ISet<string> pluginIds, ISet<string> unpinned, IDictionary<string, string> incompatible, bool pinHost)
    {
        // Accepted nodes = the flattened result (one version per id).
        var accepted = new Dictionary<string, GraphNode<RemoteResolveResult>>(StringComparer.OrdinalIgnoreCase);
        var unresolved = new List<GraphNode<RemoteResolveResult>>();
        void Visit(GraphNode<RemoteResolveResult> n)
        {
            if (Environment.GetEnvironmentVariable("SPIKE_DUMP") == "1")
                Console.WriteLine($"    node {n.Key.Name} {n.Key.VersionRange} -> {n.Item?.Key} {n.Disposition} parent={n.OuterNode?.Key.Name}");
            if (n.OuterNode is not null && n.Disposition == Disposition.Accepted && n.Item is not null)
            {
                if (n.Item.Key.Type == LibraryType.Unresolved) unresolved.Add(n);
                else accepted.TryAdd(n.Key.Name, n);
            }
            foreach (var c in n.InnerNodes) Visit(c);
        }
        Visit(graph);

        // Reachability and "who asked for what" come from each accepted package's *declared* dependencies
        // (RemoteResolveResult.Dependencies): the walker drops rejected/downgraded edges from InnerNodes.
        var requests = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var ranges = new Dictionary<string, List<VersionRange>>(StringComparer.OrdinalIgnoreCase);
        void Request(string id, string by, VersionRange? r)
        {
            (requests.TryGetValue(id, out var l) ? l : requests[id] = new()).Add($"{by} -> {r?.PrettyPrint()}");
            if (r is not null) (ranges.TryGetValue(id, out var rl) ? rl : ranges[id] = new()).Add(r);
        }
        foreach (var d in graph.Item.Data.Dependencies.Where(d => pluginIds.Contains(d.Name)))
            Request(d.Name, "(install request)", d.LibraryRange.VersionRange);
        var reachable = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>(pluginIds);
        while (queue.TryDequeue(out var id))
        {
            if (!seen.Add(id)) continue;
            reachable.Add(id);
            if (accepted.TryGetValue(id, out var node) && node.Item.Data.Match?.Provider is not HostDependencyProvider)
                foreach (var d in node.Item.Data.Dependencies)
                {
                    Request(d.Name, $"{id} {node.Item.Key.Version}", d.LibraryRange.VersionRange);
                    queue.Enqueue(d.Name);
                }
        }

        var result = new List<Classified>();
        foreach (var id in reachable)
        {
            var reqs = requests.GetValueOrDefault(id) ?? [];
            if (!accepted.TryGetValue(id, out var node))
            {
                var un = unresolved.FirstOrDefault(u => u.Key.Name.Equals(id, StringComparison.OrdinalIgnoreCase));
                result.Add(new Classified(id, null, un is not null ? DepClass.Unresolved : DepClass.FrameworkPruned, "-", reqs, null) { Ranges = ranges.GetValueOrDefault(id) ?? [] });
                continue;
            }
            var v = node.Item.Key.Version;
            var provider = node.Item.Data.Match?.Provider;
            var src = provider is HostDependencyProvider ? "host deps.json" : provider?.Source.Name ?? "?";
            host.Packages.TryGetValue(id, out var hp);
            DepClass cls;
            string? note = null;
            if (pluginIds.Contains(id)) cls = DepClass.Plugin;
            else if (incompatible.TryGetValue(id, out var why)) { cls = DepClass.IncompatibleHostOwned; note = why; }
            else if (provider is HostDependencyProvider) cls = hostOwned.Contains(id) ? DepClass.HostOwnedProvided : DepClass.HostSharedProvided;
            else if (PrivateDependencies.Contains(id)) cls = DepClass.PrivateMarked;
            else if (hp is not null)
            {
                if (!pinHost)
                {
                    // strategy 1 (no pins): compare afterwards
                    var hv = NuGetVersion.Parse(hp.Version);
                    var allOk = (ranges.GetValueOrDefault(id) ?? []).All(r => r.Satisfies(hv));
                    cls = allOk ? (hostOwned.Contains(id) ? DepClass.HostOwnedProvided : DepClass.HostSharedProvided)
                                : (hostOwned.Contains(id) ? DepClass.IncompatibleHostOwned : DepClass.PrivateConflict);
                    note = $"host has {hp.Version}";
                }
                else { cls = DepClass.PrivateConflict; note = $"host has {hp.Version}"; }
            }
            else cls = DepClass.PrivateMissing;
            result.Add(new Classified(id, v, cls, src, reqs, note) { Ranges = ranges.GetValueOrDefault(id) ?? [] });
        }
        return result;
    }

    static List<VersionRange> AllRanges(GraphNode<RemoteResolveResult> g, string id)
    {
        var list = new List<VersionRange>();
        void Visit(GraphNode<RemoteResolveResult> n)
        {
            if (n.OuterNode is not null && n.Key.Name.Equals(id, StringComparison.OrdinalIgnoreCase) && n.Key.VersionRange is not null) list.Add(n.Key.VersionRange);
            foreach (var c in n.InnerNodes) Visit(c);
        }
        Visit(g);
        return list;
    }

    public static void Print(ResolveOutcome o, TextWriter w)
    {
        foreach (var l in o.Log) w.WriteLine("  " + l);
        w.WriteLine($"  walks: {o.Walks}");
        foreach (var c in o.Packages)
        {
            w.WriteLine($"  {c.Id,-48} {c.Version?.ToNormalizedString() ?? "-",-10} {c.Class,-22} src={c.Source}{(c.Note is null ? "" : "  (" + c.Note + ")")}");
            foreach (var r in c.Requests) w.WriteLine($"      requested by {r}");
        }
        if (o.Pruned.Count > 0)
        {
            w.WriteLine("  pruned as framework-provided:");
            foreach (var p in o.Pruned) w.WriteLine("      " + p);
        }
        var a = o.Analysis;
        w.WriteLine($"  analysis: downgrades={a.Downgrades.Count} versionConflicts={a.VersionConflicts.Count} cycles={a.Cycles.Count}");
        foreach (var d in a.Downgrades)
            w.WriteLine($"      downgrade: {d.DowngradedFrom.GetPathWithLastRange()}  ->  {d.DowngradedTo.GetIdAndVersionOrRange()}");
        foreach (var c in a.VersionConflicts)
            w.WriteLine($"      CONFLICT: selected {c.Selected.GetPathWithLastRange()} ({c.Selected.Item?.Key.Version})  vs  {c.Conflicting.GetPathWithLastRange()}");
        foreach (var c in a.Cycles) w.WriteLine($"      cycle: {c.GetPath()}");
    }
}

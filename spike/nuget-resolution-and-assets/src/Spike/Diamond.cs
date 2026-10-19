using NuGet.Common;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Protocol.Core.Types;
using NuGet.Resolver;
using NuGet.Versioning;

namespace Spike;

/// <summary>Q3: several plugins in one compartment, resolved together to one version per package id.</summary>
public static class Diamond
{
    public static async Task RunAsync(HostInventory host, ISet<string> hostOwned, NuGetFramework fw, ILogger log)
    {
        var repos = new[] { Feeds.Repo(Feeds.NuGetOrg), Feeds.Repo(Feeds.LocalFeed) };
        var one = VersionRange.Parse("[1.0.0]");
        var scenarios = new (string Name, (string, VersionRange)[] Plugins)[]
        {
            ("A + B (LibShared >=1.0.0 and >=2.0.0; Newtonsoft 12.0.1 and 13.0.1)", [("PluginA", one), ("PluginB", one)]),
            ("B + C (LibShared >=2.0.0 and [1.0.0,2.0.0) -> range conflict)",       [("PluginB", one), ("PluginC", one)]),
            ("A + C (LibShared >=1.0.0 and [1.0.0,2.0.0) -> 1.0.0 satisfies both)",  [("PluginA", one), ("PluginC", one)]),
            ("D + E (D: LibShared >=1.0.0 direct; E -> LibMid -> LibShared >=3.0.0, one level deeper)", [("PluginD", one), ("PluginE", one)]),
        };

        foreach (var (name, plugins) in scenarios)
        {
            Console.WriteLine($"== compartment: {name}");
            Console.WriteLine("-- RemoteDependencyWalker (PackageReference semantics), host pinned");
            var r = new CompartmentResolver(host, hostOwned, repos, fw, log);
            var o = await r.ResolveAsync(plugins);
            CompartmentResolver.Print(o, Console.Out);
            ReportRangeConflicts(o);

            Console.WriteLine("-- NuGet.Resolver PackageResolver (packages.config semantics: every range must hold), DependencyBehavior.Lowest");
            await PackagesConfigResolve(repos, plugins.Select(p => p.Item1).ToArray(), fw, log);
            Console.WriteLine();
        }
    }

    /// <summary>
    /// The walker's Analyze() only flags conflicts it considers errors (NU1107-style). Also check every
    /// requested range against the chosen version (NU1608-style "outside of range" warnings) ourselves.
    /// </summary>
    static void ReportRangeConflicts(ResolveOutcome o)
    {
        foreach (var c in o.Packages.Where(c => c.Version is not null && c.Ranges.Count > 1))
        {
            var distinct = c.Ranges.Distinct().ToList();
            if (distinct.Count > 1)
                Console.WriteLine($"  DIAMOND {c.Id}: ranges {string.Join(" , ", distinct.Select(r => r.PrettyPrint()))} -> chose {c.Version}");
            var common = c.Ranges.Aggregate(VersionRange.All, (a, r) => VersionRange.CommonSubSet([a, r]));
            if (common.Equals(VersionRange.None))
                Console.WriteLine($"  RANGE CONFLICT {c.Id}: no version satisfies all of {string.Join(" , ", distinct.Select(r => r.PrettyPrint()))}");
            foreach (var (r, i) in c.Ranges.Select((r, i) => (r, i)))
                if (!r.Satisfies(c.Version!))
                    Console.WriteLine($"  RANGE VIOLATION {c.Id} {c.Version}: {c.Requests[i]}");
        }
    }

    static async Task PackagesConfigResolve(IReadOnlyList<SourceRepository> repos, string[] targets, NuGetFramework fw, ILogger log)
    {
        using var cache = new SourceCacheContext();
        var available = new HashSet<SourcePackageDependencyInfo>(PackageIdentityComparer.Default);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>(targets);
        while (queue.TryDequeue(out var id))
        {
            if (!seen.Add(id)) continue;
            foreach (var repo in repos)
            {
                var res = await repo.GetResourceAsync<DependencyInfoResource>();
                foreach (var p in await res.ResolvePackages(id, fw, cache, log, default))
                {
                    available.Add(p);
                    foreach (var d in p.Dependencies) queue.Enqueue(d.Id);
                }
            }
        }
        var ctx = new PackageResolverContext(DependencyBehavior.Lowest, targets, targets,
            Enumerable.Empty<PackageReference>(), Enumerable.Empty<PackageIdentity>(), available,
            repos.Select(r => r.PackageSource), log);
        try
        {
            var result = new PackageResolver().Resolve(ctx, default);
            Console.WriteLine($"  resolved ({available.Count} candidate versions gathered): {string.Join(", ", result.Select(p => p.ToString()))}");
        }
        catch (NuGetResolverConstraintException ex) { Console.WriteLine($"  FAILED (NuGetResolverConstraintException): {ex.Message}"); }
        catch (Exception ex) { Console.WriteLine($"  FAILED ({ex.GetType().Name}): {ex.Message}"); }
    }
}

using System.Diagnostics;
using NuGet.Common;
using NuGet.Protocol.Core.Types;

namespace Spike;

/// <summary>Q6: package-type filtered search on nuget.org and on a local folder feed.</summary>
public static class Search
{
    public static async Task RunAsync(string[] args, ILogger log)
    {
        var cases = new (string Source, string Term, string? Type)[]
        {
            (Feeds.NuGetOrg, "serilog", null),
            (Feeds.NuGetOrg, "serilog", "DotnetTool"),
            (Feeds.NuGetOrg, "", "MyAppPlugin"),          // custom type nobody uses on nuget.org
            (Feeds.NuGetOrg, "", "Template"),
            (Feeds.LocalFeed, "", null),
            (Feeds.LocalFeed, "", "MyAppPlugin"),
            (Feeds.LocalFeed, "plugin", "Dependency"),
        };
        foreach (var (source, term, type) in cases)
        {
            var repo = Feeds.Repo(source);
            var res = await repo.GetResourceAsync<PackageSearchResource>();
            var filter = new SearchFilter(includePrerelease: false) { PackageType = type };
            var sw = Stopwatch.StartNew();
            var results = (await res.SearchAsync(term, filter, 0, 10, log, default)).ToList();
            Console.WriteLine($"== {(source == Feeds.NuGetOrg ? "nuget.org" : "local feed")} term='{term}' packageType={type ?? "(none)"}  resource={res.GetType().Name} SupportsPackageTypeFiltering={res.SupportsPackageTypeFiltering}  ({sw.ElapsedMilliseconds} ms)");
            foreach (var r in results)
            {
                // IPackageSearchMetadata has no PackageTypes; for client-side filtering the nuspec (or the
                // FindPackageByIdResource/LocalPackageInfo) is needed. On a local feed the reader is cheap.
                Console.WriteLine($"    {r.Identity,-45} {r.Title}");
            }
            if (results.Count == 0) Console.WriteLine("    (no results)");
        }

        // Client-side check on a local feed: read package types from each nupkg's nuspec.
        Console.WriteLine("== local feed, client-side package type from nuspec");
        foreach (var f in Directory.GetFiles(Feeds.LocalFeed, "*.nupkg").Order())
        {
            using var reader = new NuGet.Packaging.PackageArchiveReader(f);
            var types = reader.GetPackageTypes();
            Console.WriteLine($"    {Path.GetFileName(f),-30} {string.Join(",", types.Select(t => t.Name))}");
        }
    }
}

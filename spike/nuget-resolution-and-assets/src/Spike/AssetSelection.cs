using System.Text.Json;
using NuGet.Client;
using NuGet.Common;
using NuGet.ContentModel;
using NuGet.Frameworks;
using NuGet.Packaging.Core;
using NuGet.RuntimeModel;
using NuGet.Versioning;

namespace Spike;

public sealed record SelectedAssets(List<string> Runtime, List<string> Native, List<(string Path, string Locale)> Resources, string? RuntimeGroup, string? NativeGroup);

/// <summary>
/// Q4: select lib/ + runtimes/ assets of an extracted package for (framework, RID) with NuGet's own
/// ContentModel rules (ManagedCodeConventions) — the same machinery restore's LockFileUtils uses.
/// </summary>
public static class AssetSelector
{
    // The SDK's PortableRuntimeIdentifierGraph.json, embedded (there is no SDK on end-user machines).
    static readonly Lazy<RuntimeGraph> Graph = new(() =>
    {
        using var s = typeof(AssetSelector).Assembly.GetManifestResourceStream("rid-graph.json")!;
        return JsonRuntimeFormat.ReadRuntimeGraph(s);
    });

    public static RuntimeGraph RidGraph => Graph.Value;

    public static SelectedAssets Select(string packageDir, NuGetFramework framework, string? rid)
    {
        // Paths relative to the package root with '/' separators, like the zip entries.
        var files = Directory.GetFiles(packageDir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(packageDir, f).Replace('\\', '/'))
            .Where(f => f.StartsWith("lib/") || f.StartsWith("runtimes/"))   // architecture §5: only lib/ and runtimes/
            .ToList();
        var conventions = new ManagedCodeConventions(RidGraph);
        var items = new ContentItemCollection();
        items.Load(files);

        // With a RID: criteria = (tfm, rid), (tfm, each RID it imports...), then (tfm, no RID). Without: tfm only.
        var criteria = rid is null ? conventions.Criteria.ForFramework(framework) : conventions.Criteria.ForFrameworkAndRuntime(framework, rid);

        var rt = items.FindBestItemGroup(criteria, conventions.Patterns.RuntimeAssemblies);
        var nat = items.FindBestItemGroup(criteria, conventions.Patterns.NativeLibraries);
        var res = items.FindBestItemGroup(criteria, conventions.Patterns.ResourceAssemblies);

        static bool Real(ContentItem i) => !i.Path.EndsWith("/_._");   // "_._" = empty folder marker: "nothing needed for this TFM"
        static string Describe(ContentItemGroup? g) => g is null ? "-" : string.Join(",", g.Properties.Select(p => $"{p.Key}={p.Value}"));
        return new SelectedAssets(
            rt?.Items.Where(Real).Select(i => i.Path).ToList() ?? [],
            nat?.Items.Where(Real).Select(i => i.Path).ToList() ?? [],
            res?.Items.Where(Real).Select(i => (i.Path, (string)i.Properties["locale"])).ToList() ?? [],
            Describe(rt), Describe(nat));
    }
}

public static class AssetSelection
{
    public static async Task RunAsync(string[] args, ILogger log)
    {
        var repos = new[] { Feeds.Repo(Feeds.NuGetOrg), Feeds.Repo(Feeds.LocalFeed) };
        if (args.Length >= 3 && args[0] != "compare")
        {
            // assets <id> <version> <tfm> [rid]
            var dir = await PackageStore.EnsureAsync(new PackageIdentity(args[0], NuGetVersion.Parse(args[1])), repos, log);
            var sel = AssetSelector.Select(dir, NuGetFramework.Parse(args[2]), args.ElementAtOrDefault(3));
            Print(args[0], sel);
            return;
        }

        // compare with the deps.json that `dotnet build -r <rid>` produced for the same packages
        var outDir = Path.Combine(Feeds.SpikeDir, "compare", "out");
        var cases = new (string Deps, string Tfm, string? Rid)[]
        {
            ("linux-x64/AssetsApp.deps.json", "net10.0", "linux-x64"),
            ("linux-musl-x64/AssetsApp.deps.json", "net10.0", "linux-musl-x64"),
            ("win-x64/AssetsApp.deps.json", "net10.0", "win-x64"),
            ("osx-arm64/AssetsApp.deps.json", "net10.0", "osx-arm64"),
            ("portable/AssetsApp.deps.json", "net10.0", null),
            ("win-portable/WinApp.deps.json", "net10.0-windows", null),       // WRONG on purpose: no platform version
            ("win-portable/WinApp.deps.json", "net10.0-windows7.0", null),    // what the SDK actually targets
            ("win10-x64/WinApp.deps.json", "net10.0-windows10.0.19041.0", "win-x64"),
        };
        int total = 0, mismatches = 0;
        foreach (var (depsRel, tfm, rid) in cases)
        {
            var deps = Path.Combine(outDir, depsRel);
            Console.WriteLine($"== {depsRel}: ours = Select(tfm {tfm}, rid {rid ?? "(none)"})");
            using var doc = JsonDocument.Parse(File.ReadAllText(deps));
            var target = doc.RootElement.GetProperty("targets").GetProperty(doc.RootElement.GetProperty("runtimeTarget").GetProperty("name").GetString()!);
            var libs = doc.RootElement.GetProperty("libraries");
            foreach (var lib in target.EnumerateObject())
            {
                if (libs.GetProperty(lib.Name).GetProperty("type").GetString() != "package") continue;
                var slash = lib.Name.IndexOf('/');
                var id = new PackageIdentity(lib.Name[..slash], NuGetVersion.Parse(lib.Name[(slash + 1)..]));
                var dir = await PackageStore.EnsureAsync(id, repos, log);
                var sel = AssetSelector.Select(dir, NuGetFramework.Parse(tfm), rid);

                var expected = new SortedSet<string>();
                foreach (var kind in new[] { "runtime", "native", "resources" })
                    if (lib.Value.TryGetProperty(kind, out var e))
                        foreach (var a in e.EnumerateObject()) expected.Add($"{kind}:{a.Name}");
                // portable apps: RID-specific assets live in runtimeTargets; the host picks per RID at startup. Not compared.
                var ours = new SortedSet<string>(sel.Runtime.Select(p => "runtime:" + p)
                    .Concat(rid is null ? [] : sel.Native.Select(p => "native:" + p))
                    .Concat(sel.Resources.Select(r => "resources:" + r.Path)));
                total++;
                var same = expected.SetEquals(ours);
                if (!same) mismatches++;
                Console.WriteLine($"  {(same ? "same" : "DIFF")}  {id,-62} {string.Join(" ", ours.Select(Short))}");
                if (!same)
                {
                    Console.WriteLine($"        sdk only : {string.Join(" ", expected.Except(ours))}");
                    Console.WriteLine($"        ours only: {string.Join(" ", ours.Except(expected))}");
                }
            }
        }
        Console.WriteLine($"== {total} package/target combinations compared, {mismatches} differences");

        // Placeholders and framework-provided packages the SDK pruned (not in deps.json at all)
        Console.WriteLine("== System.Threading.Tasks.Extensions 4.5.4 (pruned by the SDK; contains lib/netcoreapp2.1/_._)");
        var stte = await PackageStore.EnsureAsync(new PackageIdentity("System.Threading.Tasks.Extensions", NuGetVersion.Parse("4.5.4")), repos, log);
        Print("System.Threading.Tasks.Extensions", AssetSelector.Select(stte, NuGetFramework.Parse("net10.0"), "linux-x64"));
    }

    static string Short(string s) => s.Length > 80 ? "…" + s[^79..] : s;

    static void Print(string id, SelectedAssets s)
    {
        Console.WriteLine($"  {id}: runtime group [{s.RuntimeGroup}] native group [{s.NativeGroup}]");
        foreach (var r in s.Runtime) Console.WriteLine($"    runtime  {r}");
        foreach (var n in s.Native) Console.WriteLine($"    native   {n}");
        foreach (var (p, l) in s.Resources) Console.WriteLine($"    resource {p} ({l})");
    }
}

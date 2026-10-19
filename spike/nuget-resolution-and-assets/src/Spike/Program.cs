using NuGet.Frameworks;
using NuGet.Versioning;
using Spike;

var cmd = args.FirstOrDefault() ?? "help";
var rest = args.Skip(1).ToArray();
var log = new ConsoleNuGetLogger();

// Host-owned packages for this spike: NuGet.Versioning (dependency of Abstractions) and
// Microsoft.Extensions.Logging.Abstractions (IPluginContext.Logger is an ILogger, so it must be host-owned too).
var hostOwned = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "NuGet.Versioning", "Microsoft.Extensions.Logging.Abstractions" };
foreach (var o in Opt("--owned")?.Split(',') ?? []) hostOwned.Add(o);

string? Opt(string name) { var i = Array.IndexOf(rest, name); return i >= 0 && i + 1 < rest.Length ? rest[i + 1] : null; }
bool Flag(string name) => rest.Contains(name);
string[] valued = ["--owned", "--tfm", "--rid"];
string[] Positional() => rest.Where((a, i) => !a.StartsWith("--") && (i == 0 || !valued.Contains(rest[i - 1]))).ToArray();

var host = HostInventory.FromCurrentProcess();
var framework = NuGetFramework.Parse(Opt("--tfm") ?? host.TargetFramework);

switch (cmd)
{
    case "inventory":
        host.Dump(Console.Out, Flag("--verbose"));
        InventoryExtras.Run(host);
        break;

    case "resolve":
    {
        var pos = Positional();
        var id = pos[0];
        var range = pos.Length > 1 ? VersionRange.Parse(pos[1]) : null;
        var repos = new[] { Feeds.Repo(Feeds.NuGetOrg), Feeds.Repo(Feeds.LocalFeed) };
        if (range is null)
        {
            // install semantics: latest stable version of the plugin itself
            var versions = await new FeedDependencyProvider(repos[0]).GetAllVersionsAsync(id, new(), log, default);
            var latest = versions.Where(v => !v.IsPrerelease).Max()!;
            range = new VersionRange(latest, true, latest, true);
        }
        Console.WriteLine($"== resolve {id} {range.PrettyPrint()} for {framework.GetShortFolderName()}  (host-owned: {string.Join(", ", hostOwned)})");
        var resolver = new CompartmentResolver(host, hostOwned, repos, framework, log);
        foreach (var pin in Flag("--nopin") ? new[] { false } : new[] { true, false })
        {
            Console.WriteLine(pin ? "-- strategy: host packages pinned at the root (nearest-wins), re-walk on host-shared downgrade"
                                  : "-- strategy: walk plugin alone, compare with host afterwards");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var outcome = await resolver.ResolveAsync([(id, range)], pinHost: pin);
            CompartmentResolver.Print(outcome, Console.Out);
            Console.WriteLine($"  ({sw.ElapsedMilliseconds} ms)");
        }
        break;
    }

    case "diamond":
        await Diamond.RunAsync(host, hostOwned, framework, log);
        break;

    case "assets":
        await AssetSelection.RunAsync(rest, log);
        break;

    case "download":
        await Download.RunAsync(rest, log);
        break;

    case "search":
        await Search.RunAsync(rest, log);
        break;

    default:
        Console.WriteLine("commands: inventory [--verbose] | resolve <id> [range] [--nopin] [--tfm x] | diamond | assets ... | download | search");
        break;
}

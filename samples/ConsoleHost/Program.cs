// The Alcove console sample: loads the plugins installed in a plugin root, then installs every plugin it finds on
// a feed while running (hot install: each appears in the catalog without a restart), lists their commands and runs
// them. A faulting plugin doesn't stop the rest; the sample disables it (staged), checks for updates and lists the
// changes pending until the next startup. Run it with samples/run-console-sample.sh, which packs the sample
// plugins into a local folder feed first.
//
// Usage: ConsoleHost <plugin-root> [--feed <folder-or-feed-url>]

using Alcove;
using ConsoleHost;
using ConsoleHost.Contracts;
using Microsoft.Extensions.Logging;

if (args.Length is not (1 or 3) || (args.Length == 3 && args[1] != "--feed"))
{
    Console.Error.WriteLine("Usage: ConsoleHost <plugin-root> [--feed <folder-or-feed-url>]");
    return 2;
}

var root = Path.GetFullPath(args[0]);
var feed = args.Length == 3 ? args[2] : null;

using var loggerFactory = new ConsoleLoggerFactory(LogLevel.Warning);
var options = new PluginHostOptions
{
    Root = root,
    PluginPackageType = "ConsoleHostPlugin",
    HostOwnedPackages = { "ConsoleHost.Contracts" },
    LoggerFactory = loggerFactory,
};
if (feed is not null)
{
    options.Feeds.Add(new PackageFeed(Directory.Exists(feed) ? Path.GetFullPath(feed) : feed) { Name = "sample feed" });
}

await using var host = PluginHost.Create(options);
host.PluginFaulted += (_, e) => Console.WriteLine($"! {e.Plugin.Id} faulted: {e.Fault.GetType().Name}: {e.Fault.Message}");
host.Catalog.ExtensionsAdded += (_, e) => Console.WriteLine($"+ {e.Plugin.Id}: {e.Extensions.Count} extension(s) added to the catalog");

var report = await host.ValidateAsync();
Console.WriteLine($"Validation of '{root}': {report.Errors.Count()} error(s), {report.Warnings.Count()} warning(s)");
foreach (var issue in report.Issues)
{
    Console.WriteLine($"  {issue}");
}

Console.WriteLine();
await host.LoadAsync();
Console.WriteLine($"Loaded {host.Plugins.Count(p => p.Status == PluginStatus.Loaded)} installed plugin(s).");

if (feed is not null)
{
    // Search the feed (only ConsoleHostPlugin packages), then install each plugin while running.
    Console.WriteLine();
    Console.WriteLine($"Plugins on '{feed}':");
    var found = await host.SearchAsync("");
    foreach (var result in found)
    {
        Console.WriteLine($"  {result.Id,-20} {result.Version,-7} {(result.IsCompatible ? "compatible" : "INCOMPATIBLE: " + result.IncompatibilityReason)}  {result.Description}");
    }

    Console.WriteLine();
    var progress = new Progress<InstallProgress>(p => { if (p.Step == InstallStep.Downloading) Console.WriteLine($"  downloading {p.PackageId} {p.Version}"); });
    foreach (var result in found.Where(r => r.IsCompatible))
    {
        var installed = await host.InstallAsync(result.Id, progress: progress);
        Console.WriteLine($"Install {result.Id}: {installed.Status}{(installed.Reason is null ? "" : $" ({installed.Reason})")}");
        foreach (var plugin in installed.Plugins.Where(p => !string.Equals(p.Id, result.Id, StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine($"  with its plugin dependency {plugin.Id} {plugin.Version}: {plugin.Status}");
        }
    }
}

Console.WriteLine();
Console.WriteLine("Plugins:");
foreach (var plugin in host.Plugins.OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase))
{
    Console.WriteLine($"  {plugin.Id,-20} {plugin.Version,-7} cell {"'" + plugin.Cell + "'",-20} {plugin.Status}{(plugin.Reason is null ? "" : $" ({plugin.Reason})")}");
}

Console.WriteLine();
Console.WriteLine("Commands:");
foreach (var command in host.Catalog.GetAll<ICommand, CommandAttribute>())
{
    // The metadata is read without creating the command.
    Console.WriteLine($"  {command.Metadata.Name} ({command.Plugin.Id}, cell '{command.Plugin.Cell}'): {command.Metadata.Description}");
    try
    {
        var instance = command.GetInstance();   // the singleton, or a new instance of a transient
        foreach (var line in instance.Run().Split('\n'))
        {
            Console.WriteLine($"    > {line}");
        }
    }
    catch (PluginException ex)
    {
        Console.WriteLine($"    ! {ex.Message}");
    }
}

// Updates, uninstalls and disabling are staged: nothing loaded changes, they apply at the next startup. The app
// decides what to do about a faulted plugin; this one disables it.
Console.WriteLine();
foreach (var update in await host.CheckForUpdatesAsync())
{
    if (update.IsUpdateAvailable)
    {
        Console.WriteLine($"Update {update.PluginId}: {await host.UpdateAsync(update.PluginId)}");
    }
}

foreach (var faulted in host.Plugins.Where(p => p.Status == PluginStatus.Faulted))
{
    Console.WriteLine($"Disable {faulted.Id}: {(await host.DisableAsync(faulted.Id)).Status}");
}

Console.WriteLine("Pending changes (applied at the next startup):");
var pending = await host.GetPendingChangesAsync();
foreach (var change in pending)
{
    Console.WriteLine($"  {change}");
}

if (pending.Count == 0)
{
    Console.WriteLine("  none");
}

return host.Plugins.Any(p => p.Status == PluginStatus.Loaded) ? 0 : 1;

// The ConsoleHost harness: loads the plugins installed in a plugin root and, next to them, development plugins
// straight from their build output (--alcove-dev-plugin, ALCOVE_DEV_PLUGINS, or the project's PluginUnderTest items),
// then runs every command. Only this harness enables development plugins; ConsoleHost itself never does.
//
// Usage: ConsoleHost.Harness [--root <plugin-root>] [--repeat <times>] [--alcove-dev-plugin <build output>]...
//   --repeat runs the commands again every 2 seconds (e.g. to watch Hot Reload change a development plugin).

using Alcove;
using Alcove.Development;
using ConsoleHost;
using ConsoleHost.Contracts;
using Microsoft.Extensions.Logging;

var rootIndex = Array.IndexOf(args, "--root");
var root = Path.GetFullPath(rootIndex >= 0 && rootIndex + 1 < args.Length ? args[rootIndex + 1] : Path.Combine(AppContext.BaseDirectory, "harness-root"));
var repeatIndex = Array.IndexOf(args, "--repeat");
var repeat = repeatIndex >= 0 && repeatIndex + 1 < args.Length ? int.Parse(args[repeatIndex + 1], System.Globalization.CultureInfo.InvariantCulture) : 0;

using var loggerFactory = new ConsoleLoggerFactory(LogLevel.Warning);
var options = new PluginHostOptions
{
    Root = root,
    PluginPackageType = "ConsoleHostPlugin",
    HostOwnedPackages = { "ConsoleHost.Contracts" },
    LoggerFactory = loggerFactory,
};
foreach (var plugin in options.AddDevelopmentPluginsFrom(args, Environment.GetEnvironmentVariables()))
{
    Console.WriteLine($"Development plugin: {plugin}");
}

await using var host = PluginHost.Create(options);
host.PluginFaulted += (_, e) => Console.WriteLine($"! {e.Plugin.Id} faulted: {e.Fault.GetType().Name}: {e.Fault.Message}");

var report = await host.ValidateAsync();
Console.WriteLine($"Validation of '{root}' with {options.DevelopmentPlugins.Count} development plugin(s): {report.Errors.Count} error(s), {report.Warnings.Count} warning(s)");
foreach (var issue in report.Issues)
{
    Console.WriteLine($"  {issue}");
}

await host.LoadAsync();

Console.WriteLine();
Console.WriteLine("Plugins:");
foreach (var plugin in host.Plugins.OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase))
{
    var source = plugin.IsDevelopment ? "development" : "installed";
    Console.WriteLine($"  {plugin.Id,-20} {plugin.Version,-7} {source,-11} cell {"'" + plugin.Cell + "'",-20} {plugin.Status}{(plugin.Reason is null ? "" : $" ({plugin.Reason})")}");
}

for (var run = 0; run <= repeat; run++)
{
    if (run > 0)
    {
        await Task.Delay(TimeSpan.FromSeconds(2));
    }

    Console.WriteLine();
    Console.WriteLine(run == 0 ? "Commands:" : $"Commands (again, {run}/{repeat}):");
    foreach (var command in host.Catalog.GetAll<ICommand, CommandAttribute>())
    {
        try
        {
            Console.WriteLine($"  {command.Metadata.Name} ({command.Plugin.Id}): {command.GetInstance().Run()}");
        }
        catch (PluginException ex)
        {
            Console.WriteLine($"  {command.Metadata.Name} ({command.Plugin.Id}): ! {ex.Message}");
        }
    }
}

return host.Plugins.Where(p => p.IsDevelopment).All(p => p.Status == PluginStatus.Loaded) ? 0 : 1;

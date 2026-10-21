// Usage: Fixture.Development.Harness [--root <plugin root>] [--alcove-dev-plugin <build output>]...
using Alcove;
using Alcove.Development;
using Fixture.HostContracts;

var rootIndex = Array.IndexOf(args, "--root");
var root = rootIndex >= 0 ? Path.GetFullPath(args[rootIndex + 1]) : Path.Combine(Path.GetTempPath(), "alcove-harness-" + Guid.NewGuid().ToString("N"));
var options = new PluginHostOptions { Root = root, HostOwnedPackages = { "Fixture.HostContracts" } };
foreach (var plugin in options.AddDevelopmentPluginsFrom(args, Environment.GetEnvironmentVariables()))
{
    Console.WriteLine($"DEV {plugin.OutputDirectory} project={plugin.ProjectPath}");
}

try
{
    await using var host = PluginHost.Create(options);
    foreach (var issue in (await host.ValidateAsync()).Issues)
    {
        Console.WriteLine($"ISSUE {issue}");
    }

    await host.LoadAsync();
    foreach (var plugin in host.Plugins)
    {
        Console.WriteLine($"PLUGIN {plugin.Id} {plugin.Version} {plugin.Status} development={plugin.IsDevelopment}");
    }

    foreach (var describer in host.Catalog.GetAll<IDescriber>())
    {
        Console.WriteLine($"DESCRIBE {describer.Plugin.Id}: {describer.GetInstance().Describe()}");
    }

    return host.Plugins.Count > 0 && host.Plugins.All(p => p.Status == PluginStatus.Loaded) ? 0 : 1;
}
finally
{
    if (rootIndex < 0 && Directory.Exists(root))
    {
        Directory.Delete(root, recursive: true);
    }
}

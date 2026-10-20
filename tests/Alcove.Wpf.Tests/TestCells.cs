using Alcove.Cells;
using Alcove.Hosting;
using Alcove.IntegrationTests.Infrastructure;
using Microsoft.Extensions.Logging;
using NuGet.Versioning;

namespace Alcove.Wpf.Tests;

/// <summary>Real cells over this process's host inventory, with WPF fixture assemblies (copied, so nothing in the repository is locked).</summary>
internal sealed class TestCells : IDisposable
{
    private static readonly Lazy<HostInventory> s_inventory = new(() => HostInventory.FromCurrentProcess([]));
    private readonly TempDirectory _files = new("alcove-wpf-cells");

    public TestCells()
    {
        Environment = new CellEnvironment(s_inventory.Value, Logs);
    }

    public ListLoggerFactory Logs { get; } = new();

    public CellEnvironment Environment { get; }

    public ILogger Logger => Logs.CreateLogger("Alcove.Wpf");

    /// <summary>A copy of a fixture assembly from tests/fixtures/Metadata/bin/&lt;project&gt;/.</summary>
    public string Fixture(string project, string? assemblyName = null)
    {
        var name = (assemblyName ?? project) + ".dll";
        var source = Path.Combine(TestEnvironment.FixturesDirectory, "Metadata", "bin", project, name);
        var target = _files.Combine(project, Guid.NewGuid().ToString("N"), name);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(source, target);
        return target;
    }

    public static PrivateAssembly Asset(string name, string path, string packageId, string version, bool wpfRelevant) =>
        new(name, path, packageId, NuGetVersion.Parse(version)) { WpfRelevant = wpfRelevant };

    public static CellPlan Plan(params PrivateAssembly[] assets)
    {
        var plan = new CellPlan();
        foreach (var asset in assets)
        {
            plan.Private[asset.Name] = asset;
        }

        return plan;
    }

    public void Dispose() => _files.DisposeBestEffort();   // the cells keep the copies loaded
}

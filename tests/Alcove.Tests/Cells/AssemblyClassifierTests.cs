using Alcove.Cells;
using Alcove.Hosting;
using Alcove.Storage;
using Alcove.Tests.Hosting;
using Alcove.Tests.TestSupport;
using NuGet.Versioning;

namespace Alcove.Tests.Cells;

/// <summary>
/// Classification from a host inventory (the deps.json fixture of <see cref="HostInventoryTests"/>: MyApp.Contracts
/// 2.0.0 → MyApp.Primitives, Serilog 3.1.1, the Alcove.Abstractions closure, and a framework with System.Text.Json),
/// a state file and package folders with placeholder files.
/// </summary>
public sealed class AssemblyClassifierTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly PackageLayout _layout;
    private readonly HostInventory _host;

    public AssemblyClassifierTests()
    {
        _layout = new PackageLayout(_temp.Combine("root"));
        _temp.WriteFile("host/MyApp.deps.json", HostInventoryTests.AppDepsJson);
        _temp.WriteFile("dotnet/shared/Microsoft.NETCore.App/10.0.7/Microsoft.NETCore.App.deps.json", HostInventoryTests.FrameworkDepsJson);
        var depsFiles = string.Join(';', _temp.Combine("host", "MyApp.deps.json"), _temp.Combine("dotnet", "shared", "Microsoft.NETCore.App", "10.0.7", "Microsoft.NETCore.App.deps.json"));
        _host = HostInventory.Create(new HostEnvironment(depsFiles, typeof(AssemblyClassifierTests).Assembly, "linux-x64"), ["MyApp.Contracts", "MyApp.Sdk"]);
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void ClassifiesEachDependencyByClass()
    {
        Package("Contoso.Charts", "1.0.0", "lib/net10.0/Contoso.Charts.dll", "lib/net10.0/de/Contoso.Charts.resources.dll");
        Package("Newtonsoft.Json", "13.0.3", "lib/netstandard2.0/Newtonsoft.Json.dll", "lib/net6.0/Newtonsoft.Json.dll");
        Package("SQLitePCLRaw.lib", "2.1.0", "runtimes/linux-x64/native/libe_sqlite3.so", "runtimes/win-x64/native/e_sqlite3.dll");
        var plugin = Plugin("Contoso.Charts",
            Dependency("MyApp.Contracts", "2.0.0", DependencyClass.HostOwned),
            Dependency("Serilog", "3.1.1", DependencyClass.HostShared),
            Dependency("Newtonsoft.Json", "13.0.3", DependencyClass.CellPrivate),
            Dependency("SQLitePCLRaw.lib", "2.1.0", DependencyClass.CellPrivate),
            Dependency("System.Text.Json", "9.0.0", DependencyClass.HostOwned));

        var cell = Assert.Single(Classify(plugin));

        Assert.Equal("Contoso.Charts", cell.Cell);   // a default cell is named after its plugin
        Assert.Empty(cell.Issues);
        Assert.Equal(AssemblyClass.HostOwned, Class(cell, "MyApp.Contracts"));
        Assert.DoesNotContain(cell.Assemblies, a => a.Name == "MyApp.Primitives");   // host-owned names come from the dependency set; the inventory answers the rest
        Assert.Equal(AssemblyClass.HostShared, Class(cell, "Serilog"));
        Assert.Equal(AssemblyClass.CellPrivate, Class(cell, "Newtonsoft.Json"));
        Assert.Equal(AssemblyClass.CellPrivate, Class(cell, "Contoso.Charts"));
        Assert.DoesNotContain(cell.Assemblies, a => a.Name == "System.Text.Json");   // framework-provided: skipped

        Assert.Contains("MyApp.Contracts", cell.Plan.HostOwned);
        Assert.Contains("Serilog", cell.Plan.HostShared);
        Assert.EndsWith(Path.Combine("lib", "net6.0", "Newtonsoft.Json.dll"), cell.Plan.Private["Newtonsoft.Json"].Path);
        Assert.Equal(NuGetVersion.Parse("13.0.3"), cell.Plan.Private["Newtonsoft.Json"].PackageVersion);
        Assert.Contains("Contoso.Charts.resources/de", cell.Plan.Satellites.Keys);
        Assert.Equal("SQLitePCLRaw.lib", cell.Plan.NativeLibraries["libe_sqlite3.so"].PackageId);
        Assert.Equal(["Contoso.Charts"], cell.Plan.Plugins);
    }

    [Fact]
    public void AHostSharedConflictResolvedAsPrivate_IsLoadedPrivately()
    {
        Package("Contoso.Charts", "1.0.0", "lib/net10.0/Contoso.Charts.dll");
        Package("Serilog", "4.0.0", "lib/net8.0/Serilog.dll");

        var cell = Assert.Single(Classify(Plugin("Contoso.Charts", Dependency("Serilog", "4.0.0", DependencyClass.CellPrivate))));

        Assert.Equal(AssemblyClass.CellPrivate, Class(cell, "Serilog"));
        Assert.DoesNotContain("Serilog", cell.Plan.HostShared);
        Assert.Contains("Serilog", cell.Plan.Private.Keys);
    }

    [Fact]
    public void AManifestPrivateDependency_IsPrivateEvenWhenTheHostHasIt()
    {
        Package("Contoso.Charts", "1.0.0", "lib/net10.0/Contoso.Charts.dll");
        Package("Serilog", "3.1.1", "lib/net7.0/Serilog.dll");
        File.WriteAllText(_layout.GetManifestPath("Contoso.Charts", NuGetVersion.Parse("1.0.0")), """{ "schemaVersion": 1, "privateDependencies": [ "serilog" ] }""");

        var cell = Assert.Single(Classify(Plugin("Contoso.Charts", Dependency("Serilog", "3.1.1", DependencyClass.HostShared))));

        Assert.Equal(AssemblyClass.CellPrivate, Class(cell, "Serilog"));
    }

    [Fact]
    public void AHostSharedPackageTheHostNowHasInALowerVersion_IsReported()
    {
        Package("Contoso.Charts", "1.0.0", "lib/net10.0/Contoso.Charts.dll");

        var cell = Assert.Single(Classify(Plugin("Contoso.Charts", Dependency("Serilog", "3.2.0", DependencyClass.HostShared))));

        Assert.Equal(ClassificationIssueKind.HostSharedDowngraded, Assert.Single(cell.Issues).Kind);
        Assert.Equal(AssemblyClass.HostShared, Class(cell, "Serilog"));   // no private copy on disk to fall back to
    }

    [Fact]
    public void HostOwnedConflicts_AreReported()
    {
        Package("Contoso.Charts", "1.0.0", "lib/net10.0/Contoso.Charts.dll");

        var cell = Assert.Single(Classify(Plugin("Contoso.Charts",
            Dependency("MyApp.Contracts", "3.0.0", DependencyClass.HostOwned),
            Dependency("MyApp.Sdk", "1.0.0", DependencyClass.HostOwned),
            Dependency("Microsoft.Extensions.DependencyInjection.Abstractions", "9.0.0", DependencyClass.HostOwned))));

        Assert.Equal(
            [(ClassificationIssueKind.HostOwnedConflict, "MyApp.Contracts"), (ClassificationIssueKind.HostOwnedMissing, "MyApp.Sdk"), (ClassificationIssueKind.HostOwnedConflict, "Microsoft.Extensions.DependencyInjection.Abstractions")],
            cell.Issues.Select(i => (i.Kind, i.PackageId)));
        Assert.Equal(AssemblyClass.HostOwned, Class(cell, "MyApp.Sdk"));
        Assert.Contains("MyApp.Sdk", cell.Plan.HostOwned);
    }

    [Fact]
    public void ACellHoldsOneVersionOfAPackage()
    {
        Package("Contoso.Charts", "1.0.0", "lib/net10.0/Contoso.Charts.dll");
        Package("Contoso.Tables", "1.0.0", "lib/net10.0/Contoso.Tables.dll");
        Package("Newtonsoft.Json", "12.0.1", "lib/netstandard2.0/Newtonsoft.Json.dll");
        Package("Newtonsoft.Json", "13.0.3", "lib/netstandard2.0/Newtonsoft.Json.dll");

        var cell = Assert.Single(Classify(
            Plugin("Contoso.Charts", Dependency("Newtonsoft.Json", "13.0.3", DependencyClass.CellPrivate)) with { Cell = "Reporting" },
            Plugin("Contoso.Tables", Dependency("Newtonsoft.Json", "12.0.1", DependencyClass.CellPrivate)) with { Cell = "Reporting" }));

        Assert.Equal("Reporting", cell.Cell);
        Assert.Equal(["Contoso.Charts", "Contoso.Tables"], cell.Plugins);
        Assert.Equal(ClassificationIssueKind.CellVersionConflict, Assert.Single(cell.Issues).Kind);
        Assert.Equal(NuGetVersion.Parse("13.0.3"), cell.Plan.Private["Newtonsoft.Json"].PackageVersion);
    }

    [Fact]
    public void PluginProvided_CoversTheTransitiveClosureAndExposedDependencies()
    {
        Package("A", "1.0.0", "lib/net10.0/A.dll");
        Package("B", "1.0.0", "lib/net10.0/B.dll");
        Package("C", "1.0.0", "lib/net10.0/C.dll");
        Package("Newtonsoft.Json", "13.0.3", "lib/netstandard2.0/Newtonsoft.Json.dll");
        var a = Plugin("A", Dependency("Newtonsoft.Json", "13.0.3", DependencyClass.CellPrivate)) with { Cell = "X", ExposedDependencies = ["Newtonsoft.Json"] };
        var b = Plugin("B", Dependency("A", "1.0.0", DependencyClass.Plugin), Dependency("Newtonsoft.Json", "13.0.3", DependencyClass.CellPrivate)) with { Cell = "Y" };
        var c = Plugin("C", Dependency("B", "1.0.0", DependencyClass.Plugin)) with { Cell = "Z" };

        var cells = Classify(a, b, c).ToDictionary(x => x.Cell);

        Assert.All(cells.Values, x => Assert.Empty(x.Issues));
        Assert.Equal("X", cells["Y"].Plan.Provided["A"]);
        Assert.Equal("X", cells["Y"].Plan.Provided["Newtonsoft.Json"]);
        Assert.DoesNotContain("Newtonsoft.Json", cells["Y"].Plan.Private.Keys);   // never a silent duplicate
        Assert.Equal("Y", cells["Z"].Plan.Provided["B"]);
        Assert.Equal("X", cells["Z"].Plan.Provided["A"]);                  // transitive
        Assert.Equal("X", cells["Z"].Plan.Provided["Newtonsoft.Json"]);
        var provided = cells["Z"].Assemblies.Single(x => x.Name == "A");
        Assert.Equal((AssemblyClass.PluginProvided, "X", "A"), (provided.Class, provided.ProviderCell, provided.ProviderPlugin));
        Assert.Empty(cells["X"].Plan.Provided);
    }

    [Fact]
    public void AConsumerNeedingAHigherExposedVersion_IsAnExposedDependencyConflict()
    {
        Package("A", "1.0.0", "lib/net10.0/A.dll");
        Package("B", "1.0.0", "lib/net10.0/B.dll");
        Package("Newtonsoft.Json", "12.0.1", "lib/netstandard2.0/Newtonsoft.Json.dll");
        Package("Newtonsoft.Json", "13.0.3", "lib/netstandard2.0/Newtonsoft.Json.dll");
        var a = Plugin("A", Dependency("Newtonsoft.Json", "12.0.1", DependencyClass.CellPrivate)) with { Cell = "X", ExposedDependencies = ["Newtonsoft.Json"] };
        var b = Plugin("B", Dependency("A", "1.0.0", DependencyClass.Plugin), Dependency("Newtonsoft.Json", "13.0.3", DependencyClass.CellPrivate)) with { Cell = "Y" };

        var y = Classify(a, b).Single(x => x.Cell == "Y");

        var issue = Assert.Single(y.Issues);
        Assert.Equal((ClassificationIssueKind.ExposedDependencyConflict, "B", "Newtonsoft.Json"), (issue.Kind, issue.PluginId, issue.PackageId));
        Assert.Equal("X", y.Plan.Provided["Newtonsoft.Json"]);
    }

    [Fact]
    public void AnExposedHostOwnedAssembly_StaysHostOwned()
    {
        Package("A", "1.0.0", "lib/net10.0/A.dll");
        Package("B", "1.0.0", "lib/net10.0/B.dll");
        var a = Plugin("A", Dependency("MyApp.Contracts", "2.0.0", DependencyClass.HostOwned)) with { Cell = "X", ExposedDependencies = ["MyApp.Contracts"] };
        var b = Plugin("B", Dependency("A", "1.0.0", DependencyClass.Plugin), Dependency("MyApp.Contracts", "2.0.0", DependencyClass.HostOwned)) with { Cell = "Y" };

        var y = Classify(a, b).Single(x => x.Cell == "Y");

        Assert.Equal(AssemblyClass.HostOwned, Class(y, "MyApp.Contracts"));
        Assert.DoesNotContain("MyApp.Contracts", y.Plan.Provided.Keys);
    }

    [Fact]
    public void MissingPackagesAndPluginDependencies_AreReported_AndDisabledPluginsAreLeftOut()
    {
        Package("B", "1.0.0", "lib/net10.0/B.dll");
        var a = Plugin("A") with { Enabled = false };
        var b = Plugin("B", Dependency("A", "1.0.0", DependencyClass.Plugin), Dependency("Gone", "1.0.0", DependencyClass.CellPrivate));

        var cell = Assert.Single(Classify(a, b));

        Assert.Equal("B", cell.Cell);
        Assert.Equal(
            [(ClassificationIssueKind.PackageMissing, "Gone"), (ClassificationIssueKind.PluginDependencyMissing, "A")],
            cell.Issues.Select(i => (i.Kind, i.PackageId)));
    }

    [Fact]
    public void WpfRelevance_FromTheStateFile_ReachesThePlan()
    {
        Package("Contoso.Charts", "1.0.0", "lib/net10.0/Contoso.Charts.dll");
        Package("Contoso.Controls", "1.0.0", "lib/net10.0/Contoso.Controls.dll");
        var plugin = Plugin("Contoso.Charts", Dependency("Contoso.Controls", "1.0.0", DependencyClass.CellPrivate) with
        {
            Assemblies = [new AssemblyRecord { Name = "Contoso.Controls", Path = "lib/net10.0/Contoso.Controls.dll", WpfRelevant = true }],
        });

        var cell = Assert.Single(Classify(plugin));

        Assert.True(cell.Plan.Private["Contoso.Controls"].WpfRelevant);
        Assert.False(cell.Plan.Private["Contoso.Charts"].WpfRelevant);
        Assert.True(cell.Assemblies.Single(a => a.Name == "Contoso.Controls").WpfRelevant);
    }

    private IReadOnlyList<CellClassification> Classify(params PluginState[] plugins) =>
        new AssemblyClassifier(_host, _layout).Classify(new StateDocument { Plugins = plugins });

    private static AssemblyClass Class(CellClassification cell, string assembly) => cell.Assemblies.Single(a => a.Name == assembly).Class;

    private void Package(string id, string version, params string[] files)
    {
        var directory = _layout.GetPackageDirectory(id, NuGetVersion.Parse(version));
        foreach (var file in files)
        {
            var path = Path.Combine(directory, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "placeholder");
        }
    }

    private static PluginState Plugin(string id, params ResolvedDependency[] dependencies) => new()
    {
        Id = id,
        Version = NuGetVersion.Parse("1.0.0"),
        ContentHash = "hash",
        Dependencies = dependencies,
    };

    private static ResolvedDependency Dependency(string id, string version, DependencyClass classification) =>
        new() { Id = id, Version = NuGetVersion.Parse(version), Classification = classification };
}

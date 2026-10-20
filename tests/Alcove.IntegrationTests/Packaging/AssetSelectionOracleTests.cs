using System.Reflection;
using System.Text.Json;
using Alcove.Cells;
using Alcove.Hosting;
using Alcove.IntegrationTests.Cells;
using Alcove.IntegrationTests.Infrastructure;
using Alcove.Packaging;
using Alcove.Storage;
using NuGet.Frameworks;
using NuGet.Versioning;

namespace Alcove.IntegrationTests.Packaging;

/// <summary>
/// Asset selection against its oracle, <c>dotnet build</c>'s <c>deps.json</c> (ported from
/// spike/nuget-resolution-and-assets): for each package and runtime identifier, the runtime, native and
/// resource assets must be the ones the SDK picked.
/// </summary>
public sealed class AssetSelectionOracleTests(AssetOracleFixture oracle) : IClassFixture<AssetOracleFixture>
{
    public static TheoryData<string, string, string> Cases
    {
        get
        {
            var data = new TheoryData<string, string, string>();
            foreach (var (id, version) in AssetOracleFixture.Packages)
            {
                foreach (var rid in AssetOracleFixture.RuntimeIdentifiers)
                {
                    data.Add(id, version, rid);
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Selection_MatchesDotnetBuild(string id, string version, string rid)
    {
        if (oracle.Unavailable.TryGetValue(id, out var reason))
        {
            Assert.Skip(reason);
        }

        using var deps = oracle.ReadDepsJson(rid);
        var expected = ExpectedAssets(deps, $"{id}/{version}", includeNative: rid != AssetOracleFixture.Portable);

        var selected = AssetSelector.Default.SelectFromDirectory(
            oracle.PackageDirectory(id, version),
            FrameworkConstants.CommonFrameworks.Net10_0,
            rid == AssetOracleFixture.Portable ? null : rid);
        var ours = new SortedSet<string>(StringComparer.Ordinal);
        ours.UnionWith(selected.RuntimeAssemblies.Select(p => "runtime:" + p));
        ours.UnionWith(selected.ResourceAssemblies.Select(r => $"resources:{r.Path} ({r.Culture})"));
        if (rid != AssetOracleFixture.Portable)
        {
            // Portable apps list RID-specific assets under runtimeTargets; the host picks one per RID at startup.
            ours.UnionWith(selected.NativeLibraries.Select(p => "native:" + p));
        }

        Assert.Equal(expected, ours);
    }

    [Fact]
    public void FixtureAssets_CoverTheInterestingCases()
    {
        // Guards the oracle itself: the fixture must exercise RID-specific libs, RID fallback for natives and satellites.
        using var linux = oracle.ReadDepsJson("linux-x64");
        using var windows = oracle.ReadDepsJson("win-x64");
        using var mac = oracle.ReadDepsJson("osx-arm64");

        Assert.Contains("runtime:runtimes/win/lib/net10.0/Fixture.Assets.dll", ExpectedAssets(windows, "Fixture.Assets/1.0.0", includeNative: true));
        Assert.Contains("native:runtimes/osx/native/libfixtureassets.dylib", ExpectedAssets(mac, "Fixture.Assets/1.0.0", includeNative: true));
        var linuxAssets = ExpectedAssets(linux, "Fixture.Assets/1.0.0", includeNative: true);
        Assert.Contains("runtime:lib/net10.0/Fixture.Assets.dll", linuxAssets);
        Assert.Contains("resources:lib/net10.0/de/Fixture.Assets.resources.dll (de)", linuxAssets);
        Assert.Contains("native:runtimes/linux-x64/native/libfixtureassets.so", linuxAssets);
    }

    [Fact]
    public async Task Cell_ResolvesSatelliteAssembliesOfACellPrivatePackage()
    {
        using var root = new PluginRootBuilder();
        root.ExtractPackage(oracle.AssetsPackage);
        var state = new StateDocument
        {
            Plugins =
            [
                new PluginState { Id = "Fixture.Assets", Version = NuGetVersion.Parse("1.0.0"), ContentHash = "test" },
            ],
        };
        await root.WriteStateAsync(state);

        // linux-x64 whatever the OS: the fixture's Windows-specific lib is a placeholder.
        var inventory = HostInventory.Create(HostEnvironment.Current with { RuntimeIdentifier = "linux-x64" }, []);
        var classification = Assert.Single(new AssemblyClassifier(inventory, root.Layout).Classify(new StateStore(root.Root).Read()));
        Assert.Empty(classification.Issues);
        Assert.Contains(CellPlan.SatelliteKey("Fixture.Assets.resources", "de"), classification.Plan.Satellites.Keys);
        Assert.Contains("libfixtureassets.so", classification.Plan.NativeLibraries.Keys);

        var (environment, _) = CellTestHelpers.CreateEnvironment();
        var cell = environment.CreateCell(classification.Cell, classification.Plan);
        var assets = cell.LoadFromAssemblyName(new AssemblyName("Fixture.Assets"));
        var hello = assets.GetType("FixtureAssets.Greeting", throwOnError: true)!.GetMethod("Hello")!;

        Assert.Equal("Hallo", hello.Invoke(null, ["de"]));
        Assert.Equal("Bonjour", hello.Invoke(null, ["fr"]));
        Assert.Equal("Hello", hello.Invoke(null, ["it"]));
        var satellite = cell.Assemblies.Single(a => a.GetName().Name == "Fixture.Assets.resources" && a.GetName().CultureName == "de");
        Assert.StartsWith(root.Root, satellite.Location, StringComparison.Ordinal);
        Assert.Equal(AnswerSource.Private, cell.GetCommittedAnswers().Assemblies[CellPlan.SatelliteKey("Fixture.Assets.resources", "de")].Source);
    }

    private static SortedSet<string> ExpectedAssets(JsonDocument deps, string library, bool includeNative)
    {
        var root = deps.RootElement;
        var target = root.GetProperty("targets").GetProperty(root.GetProperty("runtimeTarget").GetProperty("name").GetString()!);
        Assert.True(target.TryGetProperty(library, out var entry), $"dotnet build's deps.json has no '{library}'.");
        Assert.Equal("package", root.GetProperty("libraries").GetProperty(library).GetProperty("type").GetString());

        var expected = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var kind in includeNative ? new[] { "runtime", "native", "resources" } : ["runtime", "resources"])
        {
            if (!entry.TryGetProperty(kind, out var assets))
            {
                continue;
            }

            foreach (var asset in assets.EnumerateObject())
            {
                expected.Add(kind == "resources" ? $"resources:{asset.Name} ({asset.Value.GetProperty("locale").GetString()})" : $"{kind}:{asset.Name}");
            }
        }

        return expected;
    }
}

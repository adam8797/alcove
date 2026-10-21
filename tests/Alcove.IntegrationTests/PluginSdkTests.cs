using Alcove.IntegrationTests.Infrastructure;
using Alcove.Packaging;

namespace Alcove.IntegrationTests;

/// <summary>
/// Alcove.Sdk end to end: fixture plugins packed with <c>dotnet pack</c> against a local feed
/// (implementation plan, Phase 2).
/// </summary>
public sealed class PluginSdkTests(FixturePackages fixtures)
{
    private const string PackageType = "FixturePlugin";

    /// <summary>Host-owned packages: referenced by plugins, never bundled with them.</summary>
    private static readonly string[] HostOwnedAssemblies =
        ["Alcove.Abstractions.dll", "Fixture.Contracts.dll", "NuGet.Versioning.dll", "Microsoft.Extensions.Logging.Abstractions.dll", "Microsoft.Extensions.DependencyInjection.Abstractions.dll"];

    public static TheoryData<string> Plugins => [.. FixturePackages.Plugins];

    [Theory]
    [MemberData(nameof(Plugins))]
    public void Pack_SetsThePluginPackageType(string fixture)
    {
        using var package = fixtures[fixture].OpenPackage();

        Assert.Equal([PackageType], package.PackageTypes);
    }

    [Theory]
    [MemberData(nameof(Plugins))]
    public void Pack_ContainsOnlyThePluginAssemblyAndTheManifest(string fixture)
    {
        using var package = fixtures[fixture].OpenPackage();

        Assert.Contains($"{fixture}.plugin.json", package.Files);
        Assert.Contains($"lib/net10.0/{fixture}.dll", package.Files);
        Assert.DoesNotContain(package.Files, f => HostOwnedAssemblies.Contains(Path.GetFileName(f), StringComparer.OrdinalIgnoreCase));
        Assert.DoesNotContain(package.Files, f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && f != $"lib/net10.0/{fixture}.dll");
    }

    [Theory]
    [MemberData(nameof(Plugins))]
    public void Pack_KeepsTheContractsPackageAsADependency(string fixture)
    {
        using var package = fixtures[fixture].OpenPackage();

        var contracts = package.Dependency("Fixture.Contracts");
        Assert.NotNull(contracts);
        Assert.Equal("[1.0.0, 2.0.0)", contracts.VersionRange.ToNormalizedString());
        Assert.DoesNotContain("Alcove.Sdk", package.Dependencies.Select(d => d.Id));
    }

    [Theory]
    [InlineData("Fixture.EntryPointPlugin")]
    [InlineData("Fixture.CellPlugin")]
    [InlineData("Fixture.IsolatedPlugin")]
    public void Pack_KeepsADirectAbstractionsReferenceAsADependency(string fixture)
    {
        using var package = fixtures[fixture].OpenPackage();

        var abstractions = package.Dependency("Alcove.Abstractions");
        Assert.NotNull(abstractions);
        Assert.Equal(TestEnvironment.AlcoveVersion, abstractions.VersionRange.MinVersion!.ToNormalizedString());
    }

    [Fact]
    public void Pack_WithoutADirectAbstractionsReference_GetsItThroughTheContractsPackage()
    {
        // The plugin developer guide's sample references only Alcove.Sdk and the contracts package.
        using var package = fixtures["Fixture.AttributePlugin"].OpenPackage();
        using var contracts = fixtures["Fixture.Contracts"].OpenPackage();

        Assert.Null(package.Dependency("Alcove.Abstractions"));
        Assert.NotNull(contracts.Dependency("Alcove.Abstractions"));
    }

    [Fact]
    public void Manifest_EntryPointPlugin()
    {
        using var package = fixtures["Fixture.EntryPointPlugin"].OpenPackage();

        Assert.Equal(
            new PluginManifest { EntryPoint = "Fixture.EntryPointPlugin.GreeterPlugin", PackageId = "Fixture.EntryPointPlugin", Version = "1.2.3-beta.1" },
            package.ReadManifest());
    }

    [Fact]
    public void Manifest_AttributeOnlyPlugin_HasNoEntryPoint()
    {
        using var package = fixtures["Fixture.AttributePlugin"].OpenPackage();

        Assert.Equal(new PluginManifest { PackageId = "Fixture.AttributePlugin", Version = "2.1.0.5" }, package.ReadManifest());
    }

    [Fact]
    public void Manifest_CellAndPrivateDependencies()
    {
        using var package = fixtures["Fixture.CellPlugin"].OpenPackage();

        Assert.Equal(
            new PluginManifest { Cell = "Fixture.Shared", PrivateDependencies = ["Fixture.Library"], PackageId = "Fixture.CellPlugin", Version = "1.0.0" },
            package.ReadManifest());
        Assert.NotNull(package.Dependency("Fixture.Library"));
    }

    [Fact]
    public void Manifest_Isolated()
    {
        using var package = fixtures["Fixture.IsolatedPlugin"].OpenPackage();

        Assert.Equal(
            new PluginManifest { Isolated = true, EntryPoint = "Fixture.IsolatedPlugin.IsolatedPlugin", PackageId = "Fixture.IsolatedPlugin", Version = "3.0.0" },
            package.ReadManifest());
    }

    [Theory]
    [MemberData(nameof(Plugins))]
    public async Task Build_WritesTheManifestIntoTheBuildOutput_AndCopiesDependencies(string fixture)
    {
        using var package = fixtures[fixture].OpenPackage();

        // Packing built the fixture (Release): its build output loads as a development plugin.
        var output = Path.Combine(fixtures.Workspace.ProjectDirectory(fixture), "bin", "Release", "net10.0");
        using (var stream = File.OpenRead(Path.Combine(output, $"{fixture}.plugin.json")))
        {
            Assert.Equal(package.ReadManifest(), PluginManifest.Read(stream));
        }

        Assert.True(File.Exists(Path.Combine(output, $"{fixture}.deps.json")));
        Assert.DoesNotContain(Directory.GetFiles(output), f => HostOwnedAssemblies.Contains(Path.GetFileName(f), StringComparer.OrdinalIgnoreCase));
        var evaluated = await fixtures.Workspace.GetEvaluatedPropertiesAsync(fixture, ["EnableDynamicLoading", "AlcoveIsPlugin"], TestContext.Current.CancellationToken);
        Assert.Equal("true", evaluated["EnableDynamicLoading"]);
        Assert.Equal("true", evaluated["AlcoveIsPlugin"]);
    }

    [Theory]
    [InlineData("Fixture.EntryPointPlugin", "1.2.3.0")]   // Version 1.2.3-beta.1
    [InlineData("Fixture.AttributePlugin", "2.1.0.0")]    // Version 2.1.0.5: the SDK default would give 2.1.0.5
    [InlineData("Fixture.CellPlugin", "1.0.0.0")]
    [InlineData("Fixture.IsolatedPlugin", "3.0.1.0")]     // set by the project, kept
    public async Task AssemblyVersion_IsExplicitAtEvaluationTimeAndInTheAssembly(string fixture, string expected)
    {
        using var package = fixtures[fixture].OpenPackage();

        // Evaluation time is what matters for WPF's InitializeComponent URIs (spike: wpf-across-load-contexts).
        var evaluated = await fixtures.Workspace.GetEvaluatedPropertiesAsync(
            fixture, ["AssemblyVersion"], TestContext.Current.CancellationToken);
        Assert.Equal(expected, evaluated["AssemblyVersion"]);

        Assert.Equal(new Version(expected), package.ReadAssemblyName($"lib/net10.0/{fixture}.dll").Version);
    }

    [Fact]
    public async Task Build_DoesNotCopyHostOwnedAssembliesToTheOutput()
    {
        // Fixture.CellPlugin sets EnableDynamicLoading, which copies package assets to the output.
        var workspace = fixtures.Workspace;
        var clone = workspace.CloneFixture("Fixture.CellPlugin");
        var output = Path.Combine(workspace.RootDirectory, "build-output", clone);

        (await workspace.BuildAsync(clone, [$"OutDir={output}/"], TestContext.Current.CancellationToken)).EnsureSuccess();

        var files = Directory.GetFiles(output).Select(Path.GetFileName).ToList();
        Assert.Contains("Fixture.CellPlugin.dll", files);
        Assert.Contains("Fixture.Library.dll", files);
        foreach (var hostOwned in HostOwnedAssemblies)
        {
            Assert.DoesNotContain(hostOwned, files);
        }
    }

    [Fact]
    public async Task Pack_WithoutPluginPackageType_FailsWithAClearError()
    {
        var result = await fixtures.Workspace.PackAsync(
            fixtures.Workspace.CloneFixture("Fixture.MissingPackageType"), cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Process.Succeeded, result.Process.Output);
        Assert.Contains("error ALCOVE001", result.Process.Output);
        Assert.Contains("PluginPackageType", result.Process.Output);
    }

    [Theory]
    [InlineData("PluginIsolated=maybe", "ALCOVE002")]
    [InlineData(@"PluginCell=a\b", "ALCOVE003")]
    [InlineData("PluginEntryPoint=Not a type", "ALCOVE004")]
    [InlineData("FixturePrivateDependency=not/an-id", "ALCOVE005")]
    [InlineData("FixturePrivateDependency=NuGet.Versioning", "ALCOVE006")]
    [InlineData("FixturePrivateDependency=alcove.abstractions", "ALCOVE006")]
    public async Task Pack_WithInvalidPluginProperties_Fails(string property, string errorCode)
    {
        var result = await fixtures.Workspace.PackAsync(
            fixtures.Workspace.CloneFixture("Fixture.MissingPackageType"),
            ["PluginPackageType=FixturePlugin", property],
            TestContext.Current.CancellationToken);

        Assert.False(result.Process.Succeeded, result.Process.Output);
        Assert.Contains($"error {errorCode}", result.Process.Output);
    }
}

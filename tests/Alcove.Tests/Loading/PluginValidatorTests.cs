using Alcove.Internal.Hosting;
using Alcove.Internal.Install;
using Alcove.Internal.IO;
using Alcove.Internal.Loading;
using Alcove.Internal.State;
using Alcove.Tests.Hosting;
using Alcove.Tests.Metadata;
using Alcove.Tests.TestSupport;
using NuGet.Versioning;

namespace Alcove.Tests.Loading;

/// <summary>
/// Validation over a plugin root of placeholder files (WPF cases use the Metadata fixture assemblies), with the
/// host inventory of <see cref="HostInventoryTests"/> (MyApp.Contracts 2.0.0 host-owned, Serilog 3.1.1).
/// </summary>
public sealed class PluginValidatorTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly PackageLayout _layout;
    private readonly HostInventory _host;
    private readonly List<PluginState> _plugins = [];
    private readonly List<CellDefinition> _cells = [];

    public PluginValidatorTests()
    {
        _layout = new PackageLayout(_temp.Combine("root"));
        _temp.WriteFile("host/MyApp.deps.json", HostInventoryTests.AppDepsJson);
        _temp.WriteFile("dotnet/shared/Microsoft.NETCore.App/10.0.7/Microsoft.NETCore.App.deps.json", HostInventoryTests.FrameworkDepsJson);
        var depsFiles = string.Join(';', _temp.Combine("host", "MyApp.deps.json"), _temp.Combine("dotnet", "shared", "Microsoft.NETCore.App", "10.0.7", "Microsoft.NETCore.App.deps.json"));
        _host = HostInventory.Create(new HostEnvironment(depsFiles, typeof(PluginValidatorTests).Assembly, "linux-x64"), ["MyApp.Contracts"]);
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task AConsistentRoot_HasNoIssues_AndLoadsDependenciesFirst()
    {
        Plugin("C", deps: [PluginDep("B")]);
        Plugin("B", deps: [PluginDep("A"), Dependency("MyApp.Contracts", "2.0.0", DependencyClass.HostOwned)]);
        Plugin("A", deps: [Private("Newtonsoft.Json", "13.0.3")]);
        Plugin("D");

        var plan = await ValidateAsync();

        Assert.Empty(plan.Report.Issues);
        Assert.Equal(["A", "B", "C", "D"], plan.LoadOrder.Select(p => p.Id));
        Assert.Equal(["A", "B", "C", "D"], plan.Cells.Select(c => c.Cell));
    }

    [Fact]
    public void AnUnreadableStateFile_IsAGlobalError()
    {
        _temp.WriteFile("root/state.json", """{ "schemaVersion": 99 }""");

        var plan = new PluginValidator(_host, _layout, _cells).Validate(TestContext.Current.CancellationToken);

        var issue = Assert.Single(plan.Report.Issues);
        Assert.Equal((ValidationSeverity.Error, ValidationCodes.StateFileUnreadable, true), (issue.Severity, issue.Code, issue.IsGlobal));
        Assert.True(plan.Report.HasGlobalErrors);
        Assert.Empty(plan.Plugins);
    }

    [Fact]
    public async Task ChangedAndMissingFiles_AreErrors()
    {
        Plugin("A", deps: [Private("Lib", "1.0.0")]);
        Plugin("B");
        await WriteStateAsync();
        File.AppendAllText(Path.Combine(Dir("A"), "lib", "net10.0", "A.dll"), "!");
        File.Delete(Path.Combine(Dir("Lib", "1.0.0"), "lib", "net10.0", "Lib.dll"));
        File.SetLastWriteTimeUtc(Path.Combine(Dir("B"), "lib", "net10.0", "B.dll"), DateTime.UtcNow.AddDays(-1));

        var plan = new PluginValidator(_host, _layout, _cells).Validate(TestContext.Current.CancellationToken);

        Assert.Equal(
            [("A", ValidationCodes.FileChanged), ("A", ValidationCodes.FileMissing), ("B", ValidationCodes.FileChanged)],
            plan.Report.Errors.Select(e => (e.PluginId!, e.Code)).Order());
        Assert.Contains("lib/net10.0/Lib.dll", plan.Report.Errors.Single(e => e.Code == ValidationCodes.FileMissing).Message);
        Assert.Empty(plan.LoadOrder);
    }

    [Fact]
    public async Task MissingPackagesAndBadManifests_AreErrors()
    {
        Plugin("NoPackage");
        Directory.Delete(Dir("NoPackage"), recursive: true);
        Plugin("NoManifest");
        File.Delete(_layout.GetManifestPath("NoManifest", V("1.0.0")));
        Plugin("BadManifest", manifest: "{ not json");
        Plugin("FutureManifest", manifest: """{ "schemaVersion": 2 }""");
        Plugin("FutureState");
        _plugins[^1] = _plugins[^1] with { ManifestSchemaVersion = 7 };
        Plugin("MissingDependency", deps: [Private("Gone", "1.0.0")]);
        Directory.Delete(Dir("Gone", "1.0.0"), recursive: true);

        var plan = await ValidateAsync();

        Assert.Equal(
            [
                ("BadManifest", ValidationCodes.ManifestInvalid),
                ("FutureManifest", ValidationCodes.ManifestSchemaUnsupported),
                ("FutureState", ValidationCodes.ManifestSchemaUnsupported),
                ("MissingDependency", ValidationCodes.PackageMissing),
                ("NoManifest", ValidationCodes.FileMissing),   // the manifest was recorded at install
                ("NoManifest", ValidationCodes.ManifestMissing),
                ("NoPackage", ValidationCodes.PackageMissing),
            ],
            plan.Report.Errors.Select(e => (e.PluginId!, e.Code)).Order());
    }

    [Fact]
    public async Task TheDependencyGraph_MissingDisabledAndCycles_BlocksPluginsAndTheirDependents()
    {
        Plugin("NeedsMissing", deps: [PluginDep("Nowhere")]);
        Plugin("Off");
        _plugins[^1] = _plugins[^1] with { Enabled = false };
        Plugin("NeedsOff", deps: [PluginDep("Off")]);
        Plugin("X", deps: [PluginDep("Y")]);
        Plugin("Y", deps: [PluginDep("X")]);
        Plugin("Self", deps: [PluginDep("Self")]);
        Plugin("NeedsCycle", deps: [PluginDep("X")]);
        Plugin("NeedsNeedsCycle", deps: [PluginDep("NeedsCycle")]);
        Plugin("Fine");

        var plan = await ValidateAsync();

        Assert.Equal(
            [
                ("NeedsCycle", ValidationCodes.PluginDependencyNotLoadable),
                ("NeedsMissing", ValidationCodes.PluginDependencyMissing),
                ("NeedsNeedsCycle", ValidationCodes.PluginDependencyNotLoadable),
                ("NeedsOff", ValidationCodes.PluginDependencyDisabled),
                ("Self", ValidationCodes.PluginDependencyCycle),
                ("X", ValidationCodes.PluginDependencyCycle),
                ("Y", ValidationCodes.PluginDependencyCycle),
            ],
            plan.Report.Errors.Select(e => (e.PluginId!, e.Code)).Order());
        Assert.Contains("X -> Y -> X", plan.Report.Errors.First(e => e.PluginId == "X").Message);
        Assert.Equal(["Fine"], plan.LoadOrder.Select(p => p.Id));
        Assert.Equal(PluginStatus.Disabled, plan.Plugins.Single(p => p.Id == "Off").Status);
        Assert.Equal(["Fine"], plan.Cells.Select(c => c.Cell));   // blocked plugins get no cell
    }

    [Fact]
    public async Task APluginDependencyRangeTheInstalledVersionDoesntSatisfy_BlocksTheDependentOnly()
    {
        Plugin("A");   // installed: 1.0.0
        Plugin("Fits", deps: [Ranged("A", "1.0.0", DependencyClass.Plugin, ("Fits", "[1.0.0, 2.0.0)"))]);
        Plugin("NeedsA2", deps: [Ranged("A", "2.0.0", DependencyClass.Plugin, ("NeedsA2", "[2.0.0, 3.0.0)"))]);
        Plugin("UsesNeedsA2", deps: [PluginDep("NeedsA2")]);

        var plan = await ValidateAsync();

        Assert.Equal(
            [("NeedsA2", ValidationCodes.PluginDependencyConflict), ("UsesNeedsA2", ValidationCodes.PluginDependencyNotLoadable)],
            plan.Report.Errors.Select(e => (e.PluginId!, e.Code)).Order());
        var message = plan.Report.Errors.Single(e => e.PluginId == "NeedsA2").Message;
        Assert.Contains("NeedsA2 needs the plugin A [2.0.0, 3.0.0)", message);
        Assert.Contains("version 1.0.0 is installed", message);
        Assert.Equal(["A", "Fits"], plan.LoadOrder.Select(p => p.Id));
    }

    [Fact]
    public async Task HostOwnedConflicts_AndExposedDependencyConflicts_BlockThePlugin()
    {
        Plugin("TooNew", deps: [Dependency("MyApp.Contracts", "3.0.0", DependencyClass.HostOwned)]);
        Plugin("Provider", deps: [Private("Dep", "2.0.0")]);
        _plugins[^1] = _plugins[^1] with { ExposedDependencies = ["Dep"] };
        Plugin("Consumer", deps: [PluginDep("Provider"), Private("Dep", "3.0.0")]);
        Plugin("UsesConsumer", deps: [PluginDep("Consumer")]);

        var plan = await ValidateAsync();

        Assert.Equal(
            [
                ("Consumer", ValidationCodes.ExposedDependencyConflict),
                ("TooNew", ValidationCodes.HostOwnedConflict),
                ("UsesConsumer", ValidationCodes.PluginDependencyNotLoadable),
            ],
            plan.Report.Errors.Select(e => (e.PluginId!, e.Code)).Order());
        Assert.Equal(["Provider"], plan.LoadOrder.Select(p => p.Id));
        Assert.Contains("Dep 3.0.0", plan.Plugins.Single(p => p.Id == "UsesConsumer").Reason);
    }

    [Fact]
    public async Task HostOwnedRanges_AreCheckedAgainstTheHostsVersion()
    {
        // The host has MyApp.Contracts 2.0.0.
        Plugin("Fits", deps: [Ranged("MyApp.Contracts", "2.0.0", DependencyClass.HostOwned, ("Fits", "[2.0.0, 3.0.0)"))]);
        Plugin("BuiltFor1", deps: [Ranged("MyApp.Contracts", "1.0.0", DependencyClass.HostOwned, ("BuiltFor1", "[1.0.0, 2.0.0)"))]);
        Plugin("NeedsNewer", deps: [Ranged("MyApp.Contracts", "3.0.0", DependencyClass.HostOwned, ("NeedsNewer", "[3.0.0, )"))]);
        Plugin("ThroughALibrary", deps:
        [
            Private("Lib", "1.0.0") with { RequestedBy = [Request("ThroughALibrary", "[1.0.0, )")] },
            Ranged("MyApp.Contracts", "2.0.0", DependencyClass.HostOwned, ("ThroughALibrary", "[2.0.0, )"), ("Lib", "[2.5.0, )")),
        ]);

        var plan = await ValidateAsync();

        Assert.Equal(["BuiltFor1", "NeedsNewer", "ThroughALibrary"], plan.Report.Errors.Select(e => e.PluginId!).Order());
        Assert.All(plan.Report.Errors, e => Assert.Equal(ValidationCodes.HostOwnedConflict, e.Code));
        var builtFor1 = plan.Report.Errors.Single(e => e.PluginId == "BuiltFor1").Message;
        Assert.Contains("MyApp.Contracts [1.0.0, 2.0.0)", builtFor1);
        Assert.Contains("the host has 2.0.0", builtFor1);
        Assert.Contains("declared by its dependency Lib", plan.Report.Errors.Single(e => e.PluginId == "ThroughALibrary").Message);
        Assert.Equal(["Fits"], plan.LoadOrder.Select(p => p.Id));
    }

    [Fact]
    public async Task HostSharedRanges_TheHostNoLongerSatisfies_AreErrors()
    {
        // The host has Serilog 3.1.1 and no Contoso.Missing.
        Plugin("Fits", deps: [Ranged("Serilog", "3.1.1", DependencyClass.HostShared, ("Fits", "[3.0.0, )"))]);
        Plugin("TooOld", deps: [Ranged("Serilog", "3.1.1", DependencyClass.HostShared, ("TooOld", "[3.0.0, 3.1.0)"))]);
        Plugin("Gone", deps: [Ranged("Contoso.Missing", "1.0.0", DependencyClass.HostShared, ("Gone", "[1.0.0, )"))]);

        var plan = await ValidateAsync();

        Assert.Equal(["Gone", "TooOld"], plan.Report.Errors.Select(e => e.PluginId!).Order());
        Assert.All(plan.Report.Errors, e =>
        {
            Assert.Equal(ValidationCodes.HostSharedConflict, e.Code);
            Assert.Contains("reinstall", e.Message);
        });
        Assert.Contains("Serilog [3.0.0, 3.1.0)", plan.Report.Errors.Single(e => e.PluginId == "TooOld").Message);
        Assert.Contains("the host now has 3.1.1", plan.Report.Errors.Single(e => e.PluginId == "TooOld").Message);
        Assert.Equal(["Fits"], plan.LoadOrder.Select(p => p.Id));
    }

    [Fact]
    public async Task FrameworkProvidedPackages_ArentRangeChecked()
    {
        Plugin("A", deps: [Ranged("System.Text.Json", "9.0.0", DependencyClass.HostOwned, ("A", "[9.0.0, 9.1.0)"))]);

        var plan = await ValidateAsync();

        Assert.Empty(plan.Report.Issues);
    }

    [Fact]
    public async Task TheCellRules_UseTheAssignment()
    {
        Plugin("Iso", manifest: """{ "schemaVersion": 1, "isolated": true }""");
        Plugin("Joiner", manifest: """{ "schemaVersion": 1, "cell": "Shared" }""", deps: [Private("Lib", "1.0.0")]);
        Plugin("Joiner2", manifest: """{ "schemaVersion": 1, "cell": "Shared" }""", deps: [Private("Lib", "2.0.0")]);
        _cells.Add(new CellDefinition("Shared") { Plugins = { "Iso" } });

        var plan = await ValidateAsync();

        Assert.Equal(["Iso", "Joiner", "Joiner2"], plan.Report.Errors.Select(e => e.PluginId!).Order());
        Assert.All(plan.Report.Errors, e => Assert.Equal((ValidationCodes.IsolatedPluginNotAlone, "Shared"), (e.Code, e.Cell)));
        Assert.Empty(plan.LoadOrder);
    }

    [Fact]
    public async Task DifferentVersionsInOneCell_AreAWarning()
    {
        Plugin("A", manifest: """{ "schemaVersion": 1, "cell": "Shared" }""", deps: [Private("Lib", "1.0.0")]);
        Plugin("B", manifest: """{ "schemaVersion": 1, "cell": "Shared" }""", deps: [Private("Lib", "2.0.0")]);

        var plan = await ValidateAsync();

        var warning = Assert.Single(plan.Report.Issues);
        Assert.Equal((ValidationSeverity.Warning, ValidationCodes.CellVersionConflict, "Shared"), (warning.Severity, warning.Code, warning.Cell));
        Assert.Equal(["A", "B"], plan.LoadOrder.Select(p => p.Id));
    }

    [Fact]
    public async Task NativeFileNameCollisionsAcrossCells_AreWarnings()
    {
        var nativeA = Private("NativeA", "1.0.0", "runtimes/linux-x64/native/libshared.so");
        Plugin("A", deps: [nativeA]);
        Plugin("B", deps: [Private("NativeB", "1.0.0", "runtimes/linux-x64/native/libshared.so")]);
        Plugin("C", deps: [nativeA]);   // the same file as A's

        var plan = await ValidateAsync();

        Assert.Equal(["A", "B", "C"], plan.Report.Warnings.Select(w => w.Cell!).Order());
        Assert.All(plan.Report.Warnings, w => Assert.Equal(ValidationCodes.NativeLibraryCollision, w.Code));
        Assert.Contains("libshared.so", plan.Report.Warnings.First().Message);
        Assert.False(plan.Report.HasErrors);
    }

    [Fact]
    public async Task IdenticalWpfRelevantPrivateAssemblies_AreSharedAcrossCells()
    {
        Plugin("A", deps: [PrivateFrom("Fixture.Wpf.Controls", "1.0.0", ("lib/net10.0/Fixture.Wpf.Controls.dll", MetadataFixtures.WpfControlsV1))]);
        Plugin("B", deps: [PrivateFrom("Fixture.Wpf.Controls", "1.0.0", ("lib/net10.0/Fixture.Wpf.Controls.dll", MetadataFixtures.WpfControlsV1))]);

        var plan = await ValidateAsync();

        Assert.Empty(plan.Report.Issues);
        Assert.All(plan.Cells, c => Assert.True(c.Plan.Private["Fixture.Wpf.Controls"].WpfRelevant));
    }

    [Theory]
    [InlineData("2.0.0", ValidationCodes.WpfDuplicateMayBreak)]    // a different AssemblyVersion
    [InlineData("1.0.1", ValidationCodes.WpfDuplicateWillBreak)]   // another package version with the same identity
    public async Task OtherWpfDuplicates_AreWarnings(string otherVersion, string code)
    {
        var otherFile = otherVersion == "2.0.0" ? MetadataFixtures.WpfControlsV2 : MetadataFixtures.WpfControlsV1;
        Plugin("A", deps: [PrivateFrom("Fixture.Wpf.Controls", "1.0.0", ("lib/net10.0/Fixture.Wpf.Controls.dll", MetadataFixtures.WpfControlsV1))]);
        Plugin("B", deps: [PrivateFrom("Fixture.Wpf.Controls", otherVersion, ("lib/net10.0/Fixture.Wpf.Controls.dll", otherFile))]);

        var plan = await ValidateAsync();

        Assert.Equal(["A", "B"], plan.Report.Warnings.Select(w => w.Cell!).Order());
        Assert.All(plan.Report.Warnings, w => Assert.Equal(code, w.Code));
        Assert.All(plan.Cells, c => Assert.False(c.Plan.Private["Fixture.Wpf.Controls"].WpfRelevant));
        Assert.Equal(["A", "B"], plan.LoadOrder.Select(p => p.Id));   // warnings don't block
    }

    [Theory]
    [InlineData("1.0.1", true)]    // same identity, another package version: WPF breaks the running copy's views
    [InlineData("2.0.0", false)]   // a different AssemblyVersion: "may break", not a reason to wait
    public void AWpfDuplicateAddedByAnInstall_IsAPendingRestartReasonOnlyWhenItWillBreak(string otherVersion, bool pending)
    {
        // Running: A with Fixture.Wpf.Controls 1.0.0. The install adds B with another copy of it in its own cell.
        var otherFile = otherVersion == "2.0.0" ? MetadataFixtures.WpfControlsV2 : MetadataFixtures.WpfControlsV1;
        Plugin("A", deps: [PrivateFrom("Fixture.Wpf.Controls", "1.0.0", ("lib/net10.0/Fixture.Wpf.Controls.dll", MetadataFixtures.WpfControlsV1))]);
        var current = new StateDocument { Plugins = _plugins.ToList() };
        Plugin("B", deps: [PrivateFrom("Fixture.Wpf.Controls", otherVersion, ("lib/net10.0/Fixture.Wpf.Controls.dll", otherFile))]);
        var wouldBe = new StateDocument { Plugins = _plugins.ToList() };
        var validator = new PluginValidator(_host, _layout, _cells);

        var added = HotLoadRule.Added(
            validator.Validate(current, TestContext.Current.CancellationToken).Report,
            validator.Validate(wouldBe, TestContext.Current.CancellationToken).Report);
        var reasons = HotLoadRule.BreakingIssues(added);

        Assert.NotEmpty(added);
        if (pending)
        {
            var reason = Assert.Single(reasons);
            Assert.StartsWith("It would load a duplicate of a WPF-relevant assembly that breaks plugins already running:", reason, StringComparison.Ordinal);
            Assert.Contains("Fixture.Wpf.Controls", reason);
        }
        else
        {
            Assert.Empty(reasons);
        }
    }

    private async Task<LoadPlan> ValidateAsync()
    {
        await WriteStateAsync();
        return new PluginValidator(_host, _layout, _cells).Validate(TestContext.Current.CancellationToken);
    }

    private async Task WriteStateAsync()
    {
        using var rootLock = await RootLock.AcquireAsync(_layout.Root, cancellationToken: TestContext.Current.CancellationToken);
        new StateStore(_layout.Root).Write(rootLock, new StateDocument { Plugins = _plugins.ToList() });
    }

    private static NuGetVersion V(string version) => NuGetVersion.Parse(version);

    private string Dir(string id, string version = "1.0.0") => _layout.GetPackageDirectory(id, V(version));

    /// <summary>A plugin package with a placeholder assembly and a manifest, recorded with its files.</summary>
    private void Plugin(string id, string manifest = """{ "schemaVersion": 1 }""", ResolvedDependency[]? deps = null)
    {
        WriteFiles(id, "1.0.0", [($"lib/net10.0/{id}.dll", null)]);
        File.WriteAllText(_layout.GetManifestPath(id, V("1.0.0")), manifest);
        _plugins.Add(new PluginState
        {
            Id = id,
            Version = V("1.0.0"),
            ContentHash = "hash",
            Files = Files(id, "1.0.0"),
            Assemblies = [new AssemblyRecord { Name = id, Path = $"lib/net10.0/{id}.dll" }],
            Dependencies = deps ?? [],
        });
    }

    private static DependencyRequest Request(string packageId, string range) => new(packageId, VersionRange.Parse(range));

    private static ResolvedDependency Ranged(string id, string version, DependencyClass classification, params (string By, string Range)[] requests) =>
        Dependency(id, version, classification) with { RequestedBy = requests.Select(r => Request(r.By, r.Range)).ToList() };

    private static ResolvedDependency PluginDep(string id) => Dependency(id, "1.0.0", DependencyClass.Plugin);

    private static ResolvedDependency Dependency(string id, string version, DependencyClass classification) =>
        new() { Id = id, Version = V(version), Classification = classification };

    /// <summary>A cell-private package with a placeholder assembly (or the given files), recorded with its files.</summary>
    private ResolvedDependency Private(string id, string version, params string[] files) =>
        PrivateFrom(id, version, (files.Length == 0 ? [$"lib/net10.0/{id}.dll"] : files).Select(f => (f, (string?)null)).ToArray());

    private ResolvedDependency PrivateFrom(string id, string version, params (string Path, string? Source)[] files)
    {
        WriteFiles(id, version, files);
        return Dependency(id, version, DependencyClass.CellPrivate) with { Files = Files(id, version) };
    }

    private void WriteFiles(string id, string version, (string Path, string? Source)[] files)
    {
        foreach (var (relative, source) in files)
        {
            var path = Path.Combine(Dir(id, version), relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (source is null)
            {
                File.WriteAllText(path, "placeholder");
            }
            else
            {
                File.Copy(source, path, overwrite: true);
            }
        }
    }

    private List<FileRecord> Files(string id, string version)
    {
        var directory = Dir(id, version);
        return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Select(f => new FileInfo(f))
            .Select(f => new FileRecord(Path.GetRelativePath(directory, f.FullName).Replace('\\', '/'), f.Length, f.LastWriteTimeUtc))
            .ToList();
    }
}

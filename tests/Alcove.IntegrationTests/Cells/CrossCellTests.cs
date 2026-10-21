using System.Reflection;
using System.Runtime.Loader;
using Alcove.Cells;
using Alcove.IntegrationTests.Infrastructure;
using Alcove.Storage;
using NuGet.Versioning;

namespace Alcove.IntegrationTests.Cells;

/// <summary>
/// The cross-cell spike's scenarios (spike/cross-compartment-deps), end to end: a plugin root on disk, a
/// state file, the classifier and real cells. Provider (cell X) exposes Dep 2.0's <c>Widget</c> in its API.
/// </summary>
[Collection(CellFixturesCollection.Name)]
public sealed class CrossCellTests(CellFixtures fixtures)
{
    private const string Dep = "Fixture.Dep";

    [Fact]
    public async Task Q1_AConsumerLoadedFirstGetsTheProviderFromTheProvidersCell()
    {
        using var root = new PluginRootBuilder();
        var cells = await LoadAsync(root, Provider(exposesDep: true), Consumer("Fixture.Consumer2", depVersion: 2));

        var consumer = cells["Y"].LoadFromAssemblyName(new AssemblyName("Fixture.Consumer2"));

        Assert.Equal("OK: ProviderService in 'X'", CellTestHelpers.Call(consumer, "FixtureConsumer2.Consumer2", "ServiceName"));
        Assert.Contains(cells["X"].Assemblies, a => a.GetName().Name == "Fixture.Provider");
        Assert.DoesNotContain(cells["Y"].Assemblies, a => a.GetName().Name == "Fixture.Provider");
    }

    [Fact]
    public async Task Q2b_WithoutTheExposedDependency_TheConsumersOwnCopyBreaksTypeIdentity()
    {
        using var root = new PluginRootBuilder();
        var cells = await LoadAsync(root, Provider(exposesDep: false), Consumer("Fixture.Consumer2", depVersion: 2));

        var consumer = cells["Y"].LoadFromAssemblyName(new AssemblyName("Fixture.Consumer2"));

        // The silent failure: 'is Widget' is false for an identical copy of the same file.
        Assert.StartsWith("OK: NOT my Widget", CellTestHelpers.Call(consumer, "FixtureConsumer2.Consumer2", "InspectOwnWidget"));
        Assert.Contains("InvalidCastException", CellTestHelpers.Call(consumer, "FixtureConsumer2.Consumer2", "CastProviderWidget"));
        Assert.Contains(cells["Y"].Assemblies, a => a.GetName().Name == Dep);
    }

    [Fact]
    public async Task Q2c_TheExposedDependencyResolvesThroughTheProvidersCell()
    {
        using var root = new PluginRootBuilder();
        var cells = await LoadAsync(root, Provider(exposesDep: true), Consumer("Fixture.Consumer2", depVersion: 2));

        var consumer = cells["Y"].LoadFromAssemblyName(new AssemblyName("Fixture.Consumer2"));

        Assert.Equal("OK: IS my Widget: Widget(Dep 2.0 in 'X')", CellTestHelpers.Call(consumer, "FixtureConsumer2.Consumer2", "InspectOwnWidget"));
        Assert.Equal("OK: cast ok: Widget(Dep 2.0 in 'X')", CellTestHelpers.Call(consumer, "FixtureConsumer2.Consumer2", "CastProviderWidget"));
        Assert.Equal("OK: Provider inspected Widget(Dep 2.0 in 'X')", CellTestHelpers.Call(consumer, "FixtureConsumer2.Consumer2", "PassWidget"));
        Assert.DoesNotContain(cells["Y"].Assemblies, a => a.GetName().Name == Dep);
        var answer = cells["Y"].GetCommittedAnswers().Assemblies[Dep];
        Assert.Equal((AnswerSource.Provider, "X"), (answer.Source, answer.ProviderCell));
    }

    [Fact]
    public async Task Q2c2_AConsumerNeedingAHigherExposedVersion_IsReportedAndRejectedAtBindTime()
    {
        using var root = new PluginRootBuilder();
        var state = await WriteRootAsync(root, Provider(exposesDep: true), Consumer("Fixture.Consumer3", depVersion: 3));
        var classifications = Classify(root, state);
        var cells = CreateCells(classifications);

        var conflict = Assert.Single(classifications.SelectMany(c => c.Issues), i => i.Kind == ClassificationIssueKind.ExposedDependencyConflict);
        Assert.Equal(("Y", "Fixture.Consumer3", Dep), (conflict.Cell, conflict.PluginId, conflict.PackageId));

        // The spike: the runtime accepted Dep 2.0 for a 3.0 request and failed later with MissingMethodException.
        var consumer = cells["Y"].LoadFromAssemblyName(new AssemblyName("Fixture.Consumer3"));
        var result = CellTestHelpers.Call(consumer, "FixtureConsumer3.Consumer3", "UseV3");
        Assert.StartsWith("EX: FileLoadException", result);
        Assert.Contains("lower than requested", result);
    }

    [Fact]
    public async Task Q4_ACellMapsTheTransitivePluginClosure()
    {
        // Top (Z) depends only on Middle (M), which depends on Provider (X); Top still references Provider's types.
        using var root = new PluginRootBuilder();
        var state = await WriteRootAsync(root, Provider(exposesDep: true), Middle(), Top());
        var classifications = Classify(root, state);
        var cells = CreateCells(classifications);

        var z = classifications.Single(c => c.Cell == "Z");
        Assert.Equal("X", z.Plan.Provided["Fixture.Provider"]);
        Assert.Equal("M", z.Plan.Provided["Fixture.Middle"]);
        Assert.Equal("X", z.Plan.Provided[Dep]);

        var top = cells["Z"].LoadFromAssemblyName(new AssemblyName("Fixture.Top"));
        Assert.Equal("OK: True", CellTestHelpers.Call(top, "FixtureTop.Top", "SameServiceType"));
        Assert.Equal("OK: ProviderService in 'X'", CellTestHelpers.Call(top, "FixtureTop.Top", "ServiceName"));
        Assert.Equal("M", AssemblyLoadContext.GetLoadContext(cells["M"].Assemblies.Single(a => a.GetName().Name == "Fixture.Middle"))!.Name);
    }

    [Fact]
    public async Task Q43_MappingOnlyDirectPluginDependencies_WouldFail()
    {
        // Why the closure matters: without Provider in Z's plan, Top can't bind Provider's types.
        using var root = new PluginRootBuilder();
        var state = await WriteRootAsync(root, Provider(exposesDep: true), Middle(), Top());
        var classifications = Classify(root, state);
        classifications.Single(c => c.Cell == "Z").Plan.Provided.Remove("Fixture.Provider");
        var cells = CreateCells(classifications);

        var top = cells["Z"].LoadFromAssemblyName(new AssemblyName("Fixture.Top"));

        Assert.Contains("FileNotFoundException", CellTestHelpers.Call(top, "FixtureTop.Top", "SameServiceType"));
    }

    private static PluginState Plugin(string id, string cell, params ResolvedDependency[] dependencies) => new()
    {
        Id = id,
        Version = NuGetVersion.Parse("1.0.0"),
        ContentHash = "test",
        Cell = cell,
        Assemblies = [new AssemblyRecord { Name = id, Path = $"lib/net10.0/{id}.dll" }],
        Dependencies = dependencies,
    };

    private static ResolvedDependency PluginDependency(string id) =>
        new() { Id = id, Version = NuGetVersion.Parse("1.0.0"), Classification = DependencyClass.Plugin };

    private static ResolvedDependency DepDependency(int version) =>
        new() { Id = Dep, Version = NuGetVersion.Parse($"{version}.0.0"), Classification = DependencyClass.CellPrivate, ContentHash = "test" };

    private static PluginState Provider(bool exposesDep) =>
        Plugin("Fixture.Provider", "X", DepDependency(2)) with { ExposedDependencies = exposesDep ? [Dep] : [] };

    private static PluginState Consumer(string id, int depVersion) =>
        Plugin(id, "Y", PluginDependency("Fixture.Provider"), DepDependency(depVersion));

    private static PluginState Middle() => Plugin("Fixture.Middle", "M", PluginDependency("Fixture.Provider"));

    private static PluginState Top() => Plugin("Fixture.Top", "Z", PluginDependency("Fixture.Middle"));

    private async Task<StateDocument> WriteRootAsync(PluginRootBuilder root, params PluginState[] plugins)
    {
        foreach (var plugin in plugins)
        {
            root.AddLibrary(plugin.Id, "1.0.0", fixtures.Assembly(plugin.Id));
            foreach (var dependency in plugin.Dependencies.Where(d => d.Id == Dep))
            {
                root.AddLibrary(Dep, dependency.Version.ToString(), fixtures.Assembly($"Fixture.Dep.V{dependency.Version.Major}", Dep));
            }
        }

        var state = new StateDocument { Plugins = plugins };
        await root.WriteStateAsync(state);
        return new StateStore(root.Root).Read();
    }

    private static IReadOnlyList<CellClassification> Classify(PluginRootBuilder root, StateDocument state) =>
        new AssemblyClassifier(CellTestHelpers.Inventory, root.Layout).Classify(state);

    private static Dictionary<string, Cell> CreateCells(IReadOnlyList<CellClassification> classifications)
    {
        var (environment, _) = CellTestHelpers.CreateEnvironment();
        return environment.CreateCells(classifications).ToDictionary(c => c.Name);
    }

    private async Task<Dictionary<string, Cell>> LoadAsync(PluginRootBuilder root, params PluginState[] plugins)
    {
        var state = await WriteRootAsync(root, plugins);
        var classifications = Classify(root, state);
        Assert.Empty(classifications.SelectMany(c => c.Issues));
        return CreateCells(classifications);
    }
}

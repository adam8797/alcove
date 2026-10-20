using static Alcove.Wpf.Tests.TestCells;

using Alcove.Cells;
using Alcove.Wpf.Loading;

namespace Alcove.Wpf.Tests;

/// <summary>Pre-loading a cell's WPF-relevant private assemblies when it's created (spike scenario 9), with real cells.</summary>
public sealed class PreloaderTests : IDisposable
{
    private readonly TestCells _cells = new();

    public void Dispose() => _cells.Dispose();

    [Fact]
    public void Select_TakesTheWpfRelevantOnes_ByName()
    {
        var assemblies = new[]
        {
            new CellAssembly("Zeta", "z.dll", "Zeta", NuGet.Versioning.NuGetVersion.Parse("1.0.0"), IsWpfRelevant: true),
            new CellAssembly("Plain", "p.dll", "Plain", NuGet.Versioning.NuGetVersion.Parse("1.0.0"), IsWpfRelevant: false),
            new CellAssembly("Alpha", "a.dll", "Alpha", NuGet.Versioning.NuGetVersion.Parse("1.0.0"), IsWpfRelevant: true),
        };

        Assert.Equal(["Alpha", "Zeta"], WpfPreloader.Select(assemblies).Select(a => a.Name));
    }

    [Fact]
    public void WhenACellIsCreated_ItsWpfRelevantAssembliesAreLoadedIntoIt_AndNothingElse()
    {
        _cells.Environment.AddCellCreatedCallback(cell => WpfPreloader.Preload(cell.Info, cell.Info.PrivateAssemblies, _cells.Logger));
        var plan = Plan(
            Asset("Fixture.Wpf.Controls", _cells.Fixture("Fixture.Wpf.Controls.V1", "Fixture.Wpf.Controls"), "Fixture.Wpf.Controls", "1.0.0", wpfRelevant: true),
            Asset("Fixture.Wpf.Code", _cells.Fixture("Fixture.Wpf.Code"), "Fixture.Wpf.Code", "1.0.0", wpfRelevant: true),
            Asset("Fixture.Leak.Dependency", _cells.Fixture("Fixture.Leak.Dependency"), "Fixture.Leak.Dependency", "1.0.0", wpfRelevant: false));

        var cell = _cells.Environment.CreateCell("preload", plan);

        Assert.Equal(["Fixture.Wpf.Code", "Fixture.Wpf.Controls"], cell.Assemblies.Select(a => a.GetName().Name).Order(StringComparer.Ordinal));
        Assert.Contains("Fixture.Wpf.Code, Fixture.Wpf.Controls", Assert.Single(_cells.Logs.WithEventId(6000)).Message);
    }

    [Fact]
    public void IdenticalCopiesInTwoCells_ArePreloadedOnce_AndShared()
    {
        // ADR 0015: the pre-load goes through the cell, so the core's sharing of identical WPF-relevant assemblies applies.
        _cells.Environment.AddCellCreatedCallback(cell => WpfPreloader.Preload(cell.Info, cell.Info.PrivateAssemblies, _cells.Logger));
        CellPlan Shared() => Plan(Asset("Fixture.Wpf.Controls", _cells.Fixture("Fixture.Wpf.Controls.V1", "Fixture.Wpf.Controls"), "Fixture.Wpf.Controls", "1.0.0", wpfRelevant: true));

        var first = _cells.Environment.CreateCell("shared-1", Shared());
        var second = _cells.Environment.CreateCell("shared-2", Shared());

        Assert.Same(Assert.Single(first.Assemblies), second.LoadFromAssemblyName(new System.Reflection.AssemblyName("Fixture.Wpf.Controls")));
        Assert.Empty(second.Assemblies);
    }

    [Fact]
    public void AnAssemblyThatCantLoad_IsLogged_AndTheOthersStillLoad()
    {
        var plan = Plan(
            Asset("Fixture.Wpf.Missing", Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.dll"), "Fixture.Wpf.Missing", "1.0.0", wpfRelevant: true),
            Asset("Fixture.Wpf.Code", _cells.Fixture("Fixture.Wpf.Code"), "Fixture.Wpf.Code", "1.0.0", wpfRelevant: true));
        var cell = _cells.Environment.CreateCell("preload-missing", plan);

        var loaded = WpfPreloader.Preload(cell.Info, cell.Info.PrivateAssemblies, _cells.Logger);

        Assert.Equal(["Fixture.Wpf.Code"], loaded.Select(a => a.GetName().Name));
        Assert.Contains("Fixture.Wpf.Missing", Assert.Single(_cells.Logs.WithEventId(6001)).Message);
    }
}

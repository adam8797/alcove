using System.Reflection;
using System.Runtime.Loader;
using Alcove.Wpf.Internal;
using NuGet.Versioning;
using static Alcove.Wpf.Tests.TestCells;

namespace Alcove.Wpf.Tests;

/// <summary>
/// The opt-in default-context forwarder (spike scenario 10): its choice of cell, and real forwarding through
/// <see cref="AssemblyLoadContext.Default"/>. The default context is process-wide, so these tests don't run in parallel
/// with each other, and each name is resolved through the default context by one test only.
/// </summary>
[Collection(nameof(ForwarderTests))]
[CollectionDefinition(nameof(ForwarderTests), DisableParallelization = true)]
public sealed class ForwarderTests : IDisposable
{
    private readonly TestCells _cells = new();

    public void Dispose() => _cells.Dispose();

    private static ForwardCandidate Candidate(string cell, string version, bool wpfRelevant = true, string packageId = "Lib") =>
        new(cell, new CellAssembly("Lib", $"{cell}/Lib.dll", packageId, NuGetVersion.Parse(version), wpfRelevant));

    [Fact]
    public void NoOwner_NoCell()
    {
        var decision = DefaultContextForwarder.Decide([]);

        Assert.Null(decision.Cell);
        Assert.False(decision.IsAmbiguous);
    }

    [Fact]
    public void OneOwner_IsChosen_WhateverItsVersion()
        => Assert.Equal("b", DefaultContextForwarder.Decide([Candidate("b", "2.0.0")]).Cell);

    [Fact]
    public void SeveralOwnersOfDifferentCopies_AreAmbiguous()
    {
        var decision = DefaultContextForwarder.Decide([Candidate("b", "2.0.0"), Candidate("a", "1.0.0")]);

        Assert.Null(decision.Cell);
        Assert.True(decision.IsAmbiguous);
        Assert.Equal(["a", "b"], decision.Candidates.Select(c => c.Cell));
    }

    [Fact]
    public void SeveralOwnersOfOneSharedCopy_CountAsOne()
    {
        // Identical WPF-relevant copies are one instance shared by the cells (ADR 0015): any cell gives the same.
        Assert.Equal("a", DefaultContextForwarder.Decide([Candidate("b", "1.0.0"), Candidate("a", "1.0.0")]).Cell);

        // Identical but not WPF-relevant: each cell has its own copy.
        Assert.True(DefaultContextForwarder.Decide([Candidate("b", "1.0.0", wpfRelevant: false), Candidate("a", "1.0.0", wpfRelevant: false)]).IsAmbiguous);

        // Same version of different packages: different copies.
        Assert.True(DefaultContextForwarder.Decide([Candidate("b", "1.0.0", packageId: "Other"), Candidate("a", "1.0.0")]).IsAmbiguous);
    }

    [Fact]
    public void AFailedDefaultLoad_IsForwardedToTheOneOwningCell_WhichMayGiveAHigherVersion()
    {
        // A XAML library rolled forward in its cell: BAML asks the default context for 1.0.0.0, the cell has 2.0.0.0.
        var cell = _cells.Environment.CreateCell("forward-owner", Plan(
            Asset("Fixture.Wpf.Controls", _cells.Fixture("Fixture.Wpf.Controls.V2", "Fixture.Wpf.Controls"), "Fixture.Wpf.Controls", "2.0.0", wpfRelevant: true)));
        using var forwarder = new DefaultContextForwarder(_cells.Logger);
        forwarder.Add(cell.Info);
        forwarder.Register();

        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName("Fixture.Wpf.Controls, Version=1.0.0.0"));

        Assert.Same(cell, AssemblyLoadContext.GetLoadContext(assembly));
        Assert.Equal(new Version(2, 0, 0, 0), assembly.GetName().Version);
        Assert.Contains("forward-owner", Assert.Single(_cells.Logs.WithEventId(6002)).Message);
    }

    [Fact]
    public void SeveralCellsWithDifferentCopies_AreNotResolved_AndLogged()
    {
        var v1 = _cells.Environment.CreateCell("ambiguous-1", Plan(
            Asset("Fixture.Wpf.Controls", _cells.Fixture("Fixture.Wpf.Controls.V1", "Fixture.Wpf.Controls"), "Fixture.Wpf.Controls", "1.0.0", wpfRelevant: true)));
        var v2 = _cells.Environment.CreateCell("ambiguous-2", Plan(
            Asset("Fixture.Wpf.Controls", _cells.Fixture("Fixture.Wpf.Controls.V2", "Fixture.Wpf.Controls"), "Fixture.Wpf.Controls", "2.0.0", wpfRelevant: true)));
        using var forwarder = new DefaultContextForwarder(_cells.Logger);
        forwarder.Add(v1.Info);
        forwarder.Add(v2.Info);

        Assert.Null(forwarder.OnResolving(AssemblyLoadContext.Default, new AssemblyName("Fixture.Wpf.Controls")));

        var warning = Assert.Single(_cells.Logs.WithEventId(6003)).Message;
        Assert.Contains("Fixture.Wpf.Controls 1.0.0 in cell 'ambiguous-1'; Fixture.Wpf.Controls 2.0.0 in cell 'ambiguous-2'", warning);
        Assert.Empty(v1.Assemblies.Concat(v2.Assemblies));
    }

    [Fact]
    public void NamesNoCellOwns_AndSatellites_AreLeftAlone()
    {
        var cell = _cells.Environment.CreateCell("satellite-owner", Plan(
            Asset("Fixture.Wpf.Code", _cells.Fixture("Fixture.Wpf.Code"), "Fixture.Wpf.Code", "1.0.0", wpfRelevant: true)));
        using var forwarder = new DefaultContextForwarder(_cells.Logger);
        forwarder.Add(cell.Info);

        Assert.Null(forwarder.OnResolving(AssemblyLoadContext.Default, new AssemblyName("Nobody.Owns.This")));
        Assert.Null(forwarder.OnResolving(AssemblyLoadContext.Default, new AssemblyName("Fixture.Wpf.Code, Culture=de")));
        Assert.Empty(cell.Assemblies);
        Assert.DoesNotContain(_cells.Logs.Entries, e => e.Level >= Microsoft.Extensions.Logging.LogLevel.Debug && e.Category == "Alcove.Wpf");
    }

    [Fact]
    public void AfterDispose_TheHandlerIsGone()
    {
        var cell = _cells.Environment.CreateCell("disposed-owner", Plan(
            Asset("Fixture.Wpf.Code", _cells.Fixture("Fixture.Wpf.Code"), "Fixture.Wpf.Code", "1.0.0", wpfRelevant: true)));
        var forwarder = new DefaultContextForwarder(_cells.Logger);
        forwarder.Add(cell.Info);
        forwarder.Register();
        Assert.True(forwarder.IsRegistered);

        forwarder.Dispose();

        Assert.False(forwarder.IsRegistered);
        Assert.Throws<FileNotFoundException>(() => AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName("Fixture.Wpf.Code")));
        Assert.Empty(cell.Assemblies);
        Assert.Equal(["registered", "removed"], _cells.Logs.WithEventId(6005).Select(e => e.Message.Split(' ')[^1]));
    }

    [Fact]
    public void ACellThatAnswersTheNameFromTheHost_DoesntMakeTheForwarderRecurse()
    {
        // The cell's plan lists the name as private, but it's also host-owned (checked first): the cell defers to the
        // default context, which fails and raises Resolving again on the same thread.
        var plan = Plan(Asset("Fixture.Wpf.Recursive", Path.Combine(Path.GetTempPath(), "never-loaded.dll"), "Fixture.Wpf.Recursive", "1.0.0", wpfRelevant: true));
        plan.HostOwned.Add("Fixture.Wpf.Recursive");
        var cell = _cells.Environment.CreateCell("recursive-owner", plan);
        using var forwarder = new DefaultContextForwarder(_cells.Logger);
        forwarder.Add(cell.Info);
        forwarder.Register();

        Assert.Throws<FileNotFoundException>(() => AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName("Fixture.Wpf.Recursive")));

        Assert.Contains("recursive-owner", Assert.Single(_cells.Logs.WithEventId(6004)).Message);
    }
}

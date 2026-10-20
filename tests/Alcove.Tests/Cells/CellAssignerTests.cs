using Alcove.Cells;
using Alcove.Validation;

namespace Alcove.Tests.Cells;

public sealed class CellAssignerTests
{
    [Fact]
    public void ByDefault_EachPluginGetsACellNamedAfterIt()
    {
        var assignment = CellAssigner.Assign([new("A", null, false), new("B", null, false)], []);

        Assert.Equal("A", assignment.Cells["A"]);
        Assert.Equal("B", assignment.Cells["b"]);   // plugin IDs are case-insensitive
        Assert.All(assignment.Sources.Values, s => Assert.Equal(CellSource.Default, s));
        Assert.Empty(assignment.Issues);
    }

    [Fact]
    public void PluginsRequestingTheSameCell_ShareIt()
    {
        var assignment = CellAssigner.Assign([new("A", "Shared", false), new("B", "Shared", false), new("C", null, false)], []);

        Assert.Equal(["A", "B"], assignment.Members["Shared"].Order());
        Assert.Equal(CellSource.Manifest, assignment.Sources["A"]);
        Assert.Empty(assignment.Issues);
    }

    [Fact]
    public void TheAppsOptionsWinOverTheManifest()
    {
        var assignment = CellAssigner.Assign(
            [new("A", "FromManifest", false), new("B", "FromManifest", false)],
            [new CellDefinition("FromApp") { Plugins = { "a" } }]);

        Assert.Equal("FromApp", assignment.Cells["A"]);
        Assert.Equal(CellSource.App, assignment.Sources["A"]);
        Assert.Equal("FromManifest", assignment.Cells["B"]);
    }

    [Fact]
    public void APluginInTwoAppCells_GetsTheFirstWithAWarning()
    {
        var assignment = CellAssigner.Assign(
            [new("A", null, false)],
            [new CellDefinition("One") { Plugins = { "A" } }, new CellDefinition("Two") { Plugins = { "A" } }]);

        Assert.Equal("One", assignment.Cells["A"]);
        var issue = Assert.Single(assignment.Issues);
        Assert.Equal((ValidationSeverity.Warning, ValidationCodes.CellDefinitionConflict), (issue.Severity, issue.Code));
    }

    [Fact]
    public void AnIsolatedPluginAlone_IsFine_EvenInANamedCell()
    {
        var assignment = CellAssigner.Assign(
            [new("Iso", "Mine", true), new("B", null, false)],
            [new CellDefinition("AppCell") { Plugins = { "B" } }]);

        Assert.Empty(assignment.Issues);
        Assert.Equal("Mine", assignment.Cells["Iso"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnIsolatedPluginWithOthers_IsAnErrorForEveryPluginOfThatCell(bool byTheApp)
    {
        var assignment = byTheApp
            ? CellAssigner.Assign([new("Iso", null, true), new("B", null, false), new("C", null, false)], [new CellDefinition("X") { Plugins = { "Iso", "B" } }])
            : CellAssigner.Assign([new("Iso", "X", true), new("B", "X", false), new("C", null, false)], []);

        Assert.Equal(["B", "Iso"], assignment.Issues.Select(i => i.PluginId).Order());
        Assert.All(assignment.Issues, i =>
        {
            Assert.Equal((ValidationSeverity.Error, ValidationCodes.IsolatedPluginNotAlone, "X"), (i.Severity, i.Code, i.Cell));
            Assert.Contains("Iso", i.Message);
        });
    }

    [Fact]
    public void ANamedCellWithTheNameOfAPluginsDefaultCell_JoinsIt()
    {
        // Cell names are just names: asking for cell "A" shares plugin A's default cell, and isolation still applies.
        var assignment = CellAssigner.Assign([new("A", null, true), new("B", "A", false)], []);

        Assert.Equal(2, assignment.Issues.Count(i => i.Code == ValidationCodes.IsolatedPluginNotAlone));
    }
}

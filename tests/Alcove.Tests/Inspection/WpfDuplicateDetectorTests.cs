using Alcove.Inspection;
using NuGet.Versioning;
using static Alcove.Tests.Inspection.WpfRelevanceTests;

namespace Alcove.Tests.Inspection;

/// <summary>Every branch of the WPF duplicate rule (spike: wpf-across-load-contexts §6, ADR 0015).</summary>
public sealed class WpfDuplicateDetectorTests
{
    [Fact]
    public void IdenticalPackageInTwoCells_IsSharedWithoutWarning()
    {
        var findings = WpfDuplicateDetector.Detect(
        [
            Plan("A", Info("Controls", hasXaml: true), "Controls", "1.0.0"),
            Plan("B", Info("Controls", hasXaml: true), "controls", "1.0.0"),
        ]);

        var finding = Assert.Single(findings);
        Assert.Equal(WpfDuplicateKind.ShareAcrossCells, finding.Kind);
        Assert.Equal(WpfDuplicateReason.IdenticalPackage, finding.Reason);
        Assert.Equal(["A", "B"], finding.Cells);
    }

    [Fact]
    public void SameIdentityFromDifferentPackages_WillBreak()
    {
        var findings = WpfDuplicateDetector.Detect(
        [
            Plan("A", Info("Controls", hasXaml: true), "Controls", "1.0.0"),
            Plan("B", Info("Controls", hasXaml: true), "Controls.Fork", "1.0.0"),
        ]);

        AssertConflict(findings, WpfDuplicateKind.WillBreak, WpfDuplicateReason.SameIdentity, "A", "B");
    }

    [Fact]
    public void SameIdentityFromDifferentPackageVersions_WillBreak()
    {
        // NuGet 1.0.1 kept AssemblyVersion 1.0.0.0: WPF only sees the AssemblyVersion.
        var findings = WpfDuplicateDetector.Detect(
        [
            Plan("A", Info("Controls", hasXaml: true), "Controls", "1.0.0"),
            Plan("B", Info("Controls", hasXaml: true), "Controls", "1.0.1"),
        ]);

        AssertConflict(findings, WpfDuplicateKind.WillBreak, WpfDuplicateReason.SameIdentity, "A", "B");
    }

    [Fact]
    public void SameIdentityWithoutPackageInformation_WillBreak()
    {
        var findings = WpfDuplicateDetector.Detect(
        [
            Plan("A", Info("Controls", hasXaml: true), null, null),
            Plan("B", Info("Controls", hasXaml: true), null, null),
        ]);

        AssertConflict(findings, WpfDuplicateKind.WillBreak, WpfDuplicateReason.SameIdentity, "A", "B");
    }

    [Fact]
    public void IdenticalPackageInTheDefaultContextAndACell_WillBreak()
    {
        // Sharing (ADR 0015) is between cells; a second load next to the host's copy is a real duplicate.
        var findings = WpfDuplicateDetector.Detect(
        [
            Plan(null, Info("Controls", hasXaml: true), "Controls", "1.0.0"),
            Plan("A", Info("Controls", hasXaml: true), "Controls", "1.0.0"),
        ]);

        AssertConflict(findings, WpfDuplicateKind.WillBreak, WpfDuplicateReason.SameIdentity, null, "A");
    }

    [Fact]
    public void SameAssemblyVersionDifferentToken_WillBreak()
    {
        var findings = WpfDuplicateDetector.Detect(
        [
            Plan("A", Info("Controls", hasXaml: true, token: "0123456789abcdef"), "Controls", "1.0.0"),
            Plan("B", Info("Controls", hasXaml: true), "Controls.Unsigned", "1.0.0"),
        ]);

        AssertConflict(findings, WpfDuplicateKind.WillBreak, WpfDuplicateReason.SameAssemblyVersion, "A", "B");
    }

    [Fact]
    public void DifferentAssemblyVersions_MayBreak()
    {
        var findings = WpfDuplicateDetector.Detect(
        [
            Plan("A", Info("Controls", hasXaml: true, version: "1.0.0.0"), "Controls", "1.0.0"),
            Plan("B", Info("Controls", hasXaml: true, version: "2.0.0.0"), "Controls", "2.0.0"),
        ]);

        AssertConflict(findings, WpfDuplicateKind.MayBreak, WpfDuplicateReason.DifferentAssemblyVersion, "A", "B");
    }

    [Fact]
    public void DifferentAssemblyVersionsWithUnversionedLocators_WillBreak()
    {
        var findings = WpfDuplicateDetector.Detect(
        [
            Plan("A", Info("Controls", hasXaml: true, version: "1.0.0.0"), "Controls", "1.0.0"),
            Plan("B", Info("Controls", hasXaml: true, version: "3.0.0.0", unversionedLocators: true), "Controls", "3.0.0"),
        ]);

        AssertConflict(findings, WpfDuplicateKind.WillBreak, WpfDuplicateReason.UnversionedLocators, "A", "B");
    }

    [Fact]
    public void NonWpfRelevantDuplicates_GiveNoFinding()
    {
        var findings = WpfDuplicateDetector.Detect(
        [
            Plan("A", Info("Json", hasXaml: false), "Json", "13.0.1"),
            Plan("B", Info("Json", hasXaml: false), "Json", "13.0.1"),
            Plan("C", Info("Json", hasXaml: false, version: "12.0.0.0"), "Json", "12.0.3"),
            Plan(null, Info("Json", hasXaml: false), "Json", "13.0.1"),
        ]);

        Assert.Empty(findings);
    }

    [Fact]
    public void DuplicateReferencedByXamlInTheSameCell_IsRelevant()
    {
        var lib = Info("Lib", hasXaml: false);
        var view = Info("View", hasXaml: true) with { AssemblyReferences = [lib.Identity] };

        var findings = WpfDuplicateDetector.Detect(
        [
            Plan("A", view, "View", "1.0.0"),
            Plan("A", lib, "Lib.Fork", "1.0.0"),
            Plan("B", lib, "Lib", "1.0.0"),
        ]);

        AssertConflict(findings, WpfDuplicateKind.WillBreak, WpfDuplicateReason.SameIdentity, "A", "B");
    }

    [Fact]
    public void SharedGroupPlusAnotherVersion_SharesAndWarnsAboutAllCopies()
    {
        var findings = WpfDuplicateDetector.Detect(
        [
            Plan("A", Info("Controls", hasXaml: true), "Controls", "1.0.0"),
            Plan("B", Info("Controls", hasXaml: true), "Controls", "1.0.0"),
            Plan("C", Info("Controls", hasXaml: true, version: "2.0.0.0"), "Controls", "2.0.0"),
        ]);

        Assert.Collection(findings,
            share =>
            {
                Assert.Equal(WpfDuplicateKind.ShareAcrossCells, share.Kind);
                Assert.Equal(["A", "B"], share.Cells);
            },
            conflict =>
            {
                Assert.Equal(WpfDuplicateKind.MayBreak, conflict.Kind);
                Assert.Equal(["A", "B", "C"], conflict.Cells);
            });
    }

    [Fact]
    public void SameIdentityOutranksVersionDifferences()
    {
        var findings = WpfDuplicateDetector.Detect(
        [
            Plan("A", Info("Controls", hasXaml: true), "Controls", "1.0.0"),
            Plan("B", Info("Controls", hasXaml: true), "Other", "1.0.0"),
            Plan("C", Info("Controls", hasXaml: true, version: "2.0.0.0"), "Controls", "2.0.0"),
        ]);

        AssertConflict(findings, WpfDuplicateKind.WillBreak, WpfDuplicateReason.SameIdentity, "A", "B", "C");
    }

    [Fact]
    public void SingleCopies_GiveNoFinding()
    {
        Assert.Empty(WpfDuplicateDetector.Detect(
        [
            Plan("A", Info("Controls", hasXaml: true), "Controls", "1.0.0"),
            Plan("B", Info("Other", hasXaml: true), "Other", "1.0.0"),
        ]));
    }

    [Fact]
    public void SameNameTwiceInOneContext_IsAProgrammingError()
    {
        Assert.Throws<ArgumentException>(() => WpfDuplicateDetector.Detect(
        [
            Plan("A", Info("Controls", hasXaml: true), "Controls", "1.0.0"),
            Plan("A", Info("controls", hasXaml: true, version: "2.0.0.0"), "Controls", "2.0.0"),
        ]));
    }

    [Fact]
    public void Findings_IncludeVersionsAndPaths()
    {
        var finding = Assert.Single(WpfDuplicateDetector.Detect(
        [
            Plan("A", Info("Controls", hasXaml: true, version: "1.0.0.0"), "Controls", "1.0.0"),
            Plan("B", Info("Controls", hasXaml: true, version: "2.0.0.0"), "Controls", "2.0.0"),
        ]));

        Assert.Equal([new Version(1, 0, 0, 0), new Version(2, 0, 0, 0)], finding.Copies.Select(c => c.Assembly.Identity.Version));
        Assert.Equal(["/packages/Controls/1.0.0.0/Controls.dll", "/packages/Controls/2.0.0.0/Controls.dll"], finding.Copies.Select(c => c.Assembly.Path));
        Assert.Contains("Controls, Version=2.0.0.0", finding.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RealFixtures_VersionPair_MayBreak()
    {
        var code = WpfAssemblyInspectorTests.Inspect(MetadataFixtures.WpfCode);
        var findings = WpfDuplicateDetector.Detect(
        [
            Plan("A", WpfAssemblyInspectorTests.Inspect(MetadataFixtures.WpfControlsV1), "Fixture.Wpf.Controls", "1.0.0"),
            Plan("A", code, "Fixture.Wpf.Code", "1.0.0"),
            Plan("B", WpfAssemblyInspectorTests.Inspect(MetadataFixtures.WpfControlsV2), "Fixture.Wpf.Controls", "2.0.0"),
            Plan("B", code, "Fixture.Wpf.Code", "1.0.0"),
        ]);

        Assert.Collection(findings,
            share =>
            {
                // Fixture.Wpf.Code is WPF-relevant through the BAML of Fixture.Wpf.Controls, and identical in both cells.
                Assert.Equal("Fixture.Wpf.Code", share.AssemblyName);
                Assert.Equal(WpfDuplicateKind.ShareAcrossCells, share.Kind);
            },
            conflict =>
            {
                Assert.Equal("Fixture.Wpf.Controls", conflict.AssemblyName);
                Assert.Equal(WpfDuplicateKind.MayBreak, conflict.Kind);
            });
    }

    [Fact]
    public void RealFixtures_UnversionedCopy_WillBreak()
    {
        var findings = WpfDuplicateDetector.Detect(
        [
            Plan("A", WpfAssemblyInspectorTests.Inspect(MetadataFixtures.WpfControlsV1), "Fixture.Wpf.Controls", "1.0.0"),
            Plan("B", WpfAssemblyInspectorTests.Inspect(MetadataFixtures.WpfControlsUnversioned), "Fixture.Wpf.Controls", "3.0.0"),
        ]);

        AssertConflict(findings, WpfDuplicateKind.WillBreak, WpfDuplicateReason.UnversionedLocators, "A", "B");
    }

    private static PlannedAssembly Plan(string? cell, WpfAssemblyInfo info, string? packageId, string? packageVersion) =>
        new(cell, info, packageId, packageVersion is null ? null : NuGetVersion.Parse(packageVersion));

    private static void AssertConflict(
        IReadOnlyList<WpfDuplicateFinding> findings, WpfDuplicateKind kind, WpfDuplicateReason reason, params string?[] cells)
    {
        var finding = Assert.Single(findings);
        Assert.Equal(kind, finding.Kind);
        Assert.Equal(reason, finding.Reason);
        Assert.Equal(cells, finding.Cells);
    }
}

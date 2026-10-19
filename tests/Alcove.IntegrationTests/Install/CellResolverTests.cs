using Alcove.Internal.Feeds;
using Alcove.Internal.Hosting;
using Alcove.Internal.Resolution;
using Alcove.Internal.Trust;
using Alcove.IntegrationTests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Versioning;

namespace Alcove.IntegrationTests.Install;

/// <summary>
/// <see cref="CellResolver"/> on small synthetic packages in a folder feed: range conflicts, host-owned and
/// host-shared downgrades (with the re-walk), plugins of other cells as pins, packages not found, framework pruning.
/// </summary>
public sealed class CellResolverTests : IDisposable
{
    private static readonly NuGetFramework Net10 = NuGetFramework.Parse("net10.0");
    private readonly TempDirectory _feed = new("alcove-feed");

    public void Dispose() => _feed.Dispose();

    [Fact]
    public async Task IncompatibleRangesInOneCell_AreAVersionConflictNamingThePackagesAndRanges()
    {
        Pack("Lib", "1.0.0");
        Pack("Lib", "2.0.0");
        Pack("A", "1.0.0", ("Lib", "[1.0.0, 2.0.0)"));
        Pack("B", "1.0.0", ("Lib", "2.0.0"));

        var resolution = await ResolveAsync(["A", "B"]);

        var error = Assert.Single(resolution.Errors);
        Assert.Contains("Version conflict in cell 'test'", error);
        Assert.Contains("Lib", error);
        Assert.Contains("(>= 1.0.0 && < 2.0.0)", error);
        Assert.Contains("(>= 2.0.0)", error);
        Assert.Equal(
            "Version conflict in cell 'test': A 1.0.0 needs Lib (>= 1.0.0 && < 2.0.0), but B 1.0.0 needs Lib (>= 2.0.0), and Lib 2.0.0 was selected; a cell holds one version of each package.",
            error);
    }

    [Fact]
    public async Task AHostOwnedPackageTheHostHasTooLittleOf_MakesThePluginIncompatible()
    {
        Pack("Lib", "2.0.0");
        Pack("A", "1.0.0", ("Lib", "2.0.0"));

        var resolution = await ResolveAsync(["A"], hostPins: [("Lib", "1.0.0")], hostOwned: "Lib");

        var reason = Assert.Single(resolution.Incompatibilities);
        Assert.Equal("A 1.0.0 needs Lib (>= 2.0.0), but the host has 1.0.0; Lib is host-owned, so the plugin is incompatible with this host.", reason);
    }

    [Fact]
    public async Task AHostSharedPackageTheHostHasTooLittleOf_IsUnpinnedAndBecomesCellPrivate()
    {
        Pack("Lib", "1.0.0");
        Pack("Lib", "2.0.0");
        Pack("A", "1.0.0", ("Lib", "1.0.0"));
        Pack("B", "1.0.0", ("Lib", "2.0.0"));

        var resolution = await ResolveAsync(["A", "B"], hostPins: [("Lib", "1.0.0"), ("Other", "3.0.0")]);

        Assert.Empty(resolution.Errors);
        Assert.Equal(2, resolution.Walks);
        Assert.Equal(["Lib"], resolution.Unpinned);
        Assert.Equal(["Other"], resolution.HostPinned);
        Assert.Equal(NuGetVersion.Parse("2.0.0"), resolution.Packages["Lib"].Version);
        Assert.IsType<FeedDependencyProvider>(resolution.Packages["Lib"].Provider);
    }

    [Fact]
    public async Task APluginOfAnotherCell_IsALeafPin_AndTooLowAVersionIsAnError()
    {
        Pack("Lib", "1.0.0");
        Pack("Other", "1.0.0", ("Lib", "1.0.0"));
        Pack("A", "1.0.0", ("Other", "1.0.0"));
        Pack("B", "1.0.0", ("Other", "2.0.0"));

        var leaf = await ResolveAsync(["A"], pluginPins: [("Other", "1.0.0")]);
        Assert.Empty(leaf.Errors);
        Assert.IsType<PinnedDependencyProvider>(leaf.Packages["Other"].Provider);
        Assert.False(leaf.Packages.ContainsKey("Lib"));   // resolved in Other's own cell

        var tooLow = await ResolveAsync(["B"], pluginPins: [("Other", "1.0.0")]);
        Assert.Equal("B 1.0.0 needs the plugin Other (>= 2.0.0), but Other 1.0.0 is installed (it is in another cell and isn't changed by this install).", Assert.Single(tooLow.Errors));
    }

    [Fact]
    public async Task MissingPackagesAreErrors_AndFrameworkProvidedPackagesArePruned()
    {
        Pack("A", "1.0.0", ("Missing", "1.0.0"), ("System.Text.Json", "8.0.0"));

        var resolution = await ResolveAsync(["A"]);

        var error = Assert.Single(resolution.Errors);
        Assert.StartsWith("Missing (>= 1.0.0) (needed by A 1.0.0) wasn't found on the feeds", error, StringComparison.Ordinal);
        Assert.False(resolution.Packages.ContainsKey("System.Text.Json"));
    }

    private async Task<CellResolution> ResolveAsync(
        string[] plugins,
        (string Id, string Version)[]? hostPins = null,
        (string Id, string Version)[]? pluginPins = null,
        string? hostOwned = null)
    {
        var ct = TestContext.Current.CancellationToken;
        var host = HostInventory.FromCurrentProcess([]);
        var options = new PluginHostOptions { Root = _feed.Path, Feeds = { new PackageFeed(_feed.Path) } };
        using var session = new FeedSession(FeedConfiguration.Create(options), AllowAllVerifier.Instance, NullLogger.Instance, ct);
        var providers = session.Repositories
            .Select(r => new FeedDependencyProvider(session, r, (id, range) => host.IsFrameworkProvided(id, range.MinVersion)))
            .ToList();
        var resolver = new CellResolver(session, providers, Net10, NullLogger.Instance);
        return await resolver.ResolveAsync(
            new CellResolutionRequest(
                "test",
                plugins.Select(p => new PackageIdentity(p, NuGetVersion.Parse("1.0.0"))).ToList(),
                Pins(hostPins),
                Pins(pluginPins),
                id => string.Equals(id, hostOwned, StringComparison.OrdinalIgnoreCase)),
            ct);

        static Dictionary<string, NuGetVersion> Pins((string Id, string Version)[]? pins) =>
            (pins ?? []).ToDictionary(p => p.Id, p => NuGetVersion.Parse(p.Version), StringComparer.OrdinalIgnoreCase);
    }

    private void Pack(string id, string version, params (string Id, string Range)[] dependencies)
    {
        var builder = new PackageBuilder { Id = id, Version = NuGetVersion.Parse(version), Description = id };
        builder.Authors.Add("tests");
        builder.DependencyGroups.Add(new PackageDependencyGroup(Net10, dependencies.Select(d => new PackageDependency(d.Id, VersionRange.Parse(d.Range)))));
        builder.Files.Add(new PhysicalPackageFile(new MemoryStream()) { TargetPath = "lib/net10.0/_._" });
        using var stream = File.Create(Path.Combine(_feed.Path, $"{id}.{version}.nupkg"));
        builder.Save(stream);
    }
}

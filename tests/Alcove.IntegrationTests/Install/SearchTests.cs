using Alcove.Feeds;
using Alcove.Install;
using Alcove.IntegrationTests.Infrastructure;
using Alcove.Packaging;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Versioning;

namespace Alcove.IntegrationTests.Install;

/// <summary>Search, versions and version selection on a folder feed of small synthetic plugin packages.</summary>
public sealed class SearchTests : IDisposable
{
    private const string PackageType = "FixturePlugin";
    private readonly TempDirectory _feed = new("alcove-feed");
    private readonly TempDirectory _root = new("alcove-root");
    private readonly List<PluginHost> _hosts = [];

    public SearchTests()
    {
        Pack("Plugin.Charts", "1.0.0", "Charts", plugin: true);
        Pack("Plugin.Charts", "2.0.0", "Charts", plugin: true);
        Pack("Plugin.Charts", "3.0.0-beta", "Charts", plugin: true);
        Pack("Plugin.Tables", "1.0.0", "Tables for everyone", plugin: true, ("Fixture.Missing", "1.0.0"));
        Pack("Plugin.Library", "1.0.0", "Not a plugin", plugin: false);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (var host in _hosts)
        {
            host.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        _feed.Dispose();
        _root.DisposeBestEffort();
    }

    [Fact]
    public async Task AFolderFeed_ListsEachPluginOnce_AtItsLatestVersion_WithCompatibility()
    {
        var host = Create();

        var results = await host.SearchAsync("", Ct);

        Assert.Equal(["Plugin.Charts", "Plugin.Tables"], results.Select(r => r.Id).Order(StringComparer.Ordinal));
        var charts = results.Single(r => r.Id == "Plugin.Charts");
        Assert.Equal(("2.0.0", "Charts", "tests", _feed.Path, true), (charts.Version.ToNormalizedString(), charts.Description, charts.Authors, charts.Source, charts.IsCompatible));
        var tables = results.Single(r => r.Id == "Plugin.Tables");
        Assert.False(tables.IsCompatible);
        Assert.Equal("It needs the host-owned package Fixture.Missing (>= 1.0.0), which this host doesn't have.", tables.IncompatibilityReason);

        var prerelease = await host.SearchAsync("charts", new SearchOptions { IncludePrerelease = true }, Ct);
        Assert.Equal("3.0.0-beta", Assert.Single(prerelease).Version.ToNormalizedString());
    }

    [Fact]
    public async Task VersionsAndInstalls_OnlyConsiderPrereleaseVersionsWhenAllowed()
    {
        var stable = Create();
        Assert.Equal(["1.0.0", "2.0.0"], (await stable.GetVersionsAsync("Plugin.Charts", Ct)).Select(v => v.ToNormalizedString()));
        Assert.Equal("2.0.0", (await stable.InstallAsync("Plugin.Charts", cancellationToken: Ct)).Version!.ToNormalizedString());

        using var other = new TempDirectory("alcove-root");
        var prerelease = Create(o => { o.AllowPrerelease = true; o.Root = other.Path; });
        Assert.Equal(["1.0.0", "2.0.0", "3.0.0-beta"], (await prerelease.GetVersionsAsync("Plugin.Charts", Ct)).Select(v => v.ToNormalizedString()));
        var installed = await prerelease.InstallAsync("Plugin.Charts", VersionRange.Parse("[1.0.0, 2.0.0]"), cancellationToken: Ct);
        Assert.Equal((InstallStatus.Installed, "2.0.0"), (installed.Status, installed.Version!.ToNormalizedString()));
        await prerelease.DisposeAsync();
    }

    private PluginHost Create(Action<PluginHostOptions>? configure = null)
    {
        var options = new PluginHostOptions
        {
            Root = _root.Path,
            PluginPackageType = PackageType,
            HostOwnedPackages = { "Fixture.Missing" },
            Feeds = { new PackageFeed(_feed.Path) },
        };
        configure?.Invoke(options);
        var host = PluginHost.Create(options);
        _hosts.Add(host);
        return host;
    }

    private void Pack(string id, string version, string description, bool plugin, params (string Id, string Range)[] dependencies)
    {
        var builder = new PackageBuilder { Id = id, Version = NuGetVersion.Parse(version), Description = description };
        builder.Authors.Add("tests");
        builder.DependencyGroups.Add(new PackageDependencyGroup(NuGetFramework.Parse("net10.0"), dependencies.Select(d => new PackageDependency(d.Id, VersionRange.Parse(d.Range)))));
        builder.Files.Add(new PhysicalPackageFile(new MemoryStream()) { TargetPath = "lib/net10.0/_._" });
        if (plugin)
        {
            builder.PackageTypes.Add(new PackageType(PackageType, new Version(1, 0)));
            builder.Files.Add(new PhysicalPackageFile(new MemoryStream("""{ "schemaVersion": 1 }"""u8.ToArray())) { TargetPath = PluginManifest.GetFileName(id) });
        }

        using var stream = File.Create(Path.Combine(_feed.Path, $"{id}.{version}.nupkg"));
        builder.Save(stream);
    }
}

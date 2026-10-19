using System.Security;
using System.Text.Json;
using Alcove.Internal.Feeds;
using Alcove.Internal.Hosting;
using Alcove.Internal.Resolution;
using Alcove.Internal.Trust;
using Alcove.IntegrationTests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using NuGet.Packaging.Core;
using NuGet.Versioning;

namespace Alcove.IntegrationTests.Install;

/// <summary>
/// The cell resolver (RemoteDependencyWalker + host pins, ADR 0016) against its oracle, <c>dotnet restore</c>: a
/// project referencing a cell's plugins at their exact versions (and the pinned packages as direct references) must
/// restore exactly the package set the resolver accepts. Plugin dependencies are walked like libraries here, as
/// restore does. Feeds: the packed Hosting fixtures, the workspace feed (Alcove packages, contracts) and the test
/// run's global packages folder (third-party), so no network is needed.
/// </summary>
public sealed class ResolutionOracleTests(HostingFixtures fixtures)
{
    public static TheoryData<string, string[], string[]> Cells => new()
    {
        // A diamond: Dep >= 1.0 and >= 2.0 in one cell.
        { "diamond", ["Fixture.Hosting.UserA", "Fixture.Hosting.UserB"], [] },
        // A chain of three plugins down to Dep 2.0.
        { "chain", ["Fixture.Hosting.Top"], [] },
        // Nearest wins: Consumer3 needs Dep >= 3.0 directly, Provider (one level deeper) >= 2.0.
        { "nearest", ["Fixture.Hosting.Consumer3"], [] },
        // Host pins win where they satisfy the plugins: UserA alone would get Dep 1.0 (lowest applicable), the pin
        // gives 2.0. Pins are leaves in the resolver (the host's own dependencies are the host's business), so the
        // oracle pins packages without dependencies.
        { "pinned", ["Fixture.Hosting.UserA"], ["Fixture.Dep/2.0.0", "NuGet.Versioning/7.9.0"] },
    };

    [Theory]
    [MemberData(nameof(Cells))]
    public async Task TheResolvedSet_MatchesDotnetRestore(string scenario, string[] plugins, string[] pins)
    {
        var ct = TestContext.Current.CancellationToken;
        var pinned = pins.Select(p => p.Split('/')).ToDictionary(p => p[0], p => NuGetVersion.Parse(p[1]), StringComparer.OrdinalIgnoreCase);
        var sources = new[] { fixtures.PackagesDirectory, fixtures.Workspace.FeedDirectory, FixtureWorkspace.ThirdPartySource };

        var expected = await RestoreAsync(scenario, plugins, pinned, sources, ct);
        var actual = await ResolveAsync(scenario, plugins, pinned, sources, ct);

        Assert.Equal(expected, actual);
        Assert.Contains(plugins[0] + "/1.0.0", actual);
        Assert.Contains(scenario is "diamond" or "chain" or "pinned" ? "Fixture.Dep/2.0.0" : "Fixture.Dep/3.0.0", actual);
    }

    private static async Task<IReadOnlyList<string>> ResolveAsync(string cell, string[] plugins, Dictionary<string, NuGetVersion> pins, string[] sources, CancellationToken ct)
    {
        var host = HostInventory.FromCurrentProcess([]);
        var options = new PluginHostOptions { Root = Path.GetTempPath() };
        foreach (var source in sources)
        {
            options.Feeds.Add(new PackageFeed(source));
        }

        using var session = new FeedSession(FeedConfiguration.Create(options), AllowAllVerifier.Instance, NullLogger.Instance, ct);
        var providers = session.Repositories
            .Select(r => new FeedDependencyProvider(session, r, (id, range) => host.IsFrameworkProvided(id, range.MinVersion)))
            .ToList();
        var resolver = new CellResolver(session, providers, host.Framework, NullLogger.Instance);

        var resolution = await resolver.ResolveAsync(
            new CellResolutionRequest(cell, plugins.Select(p => new PackageIdentity(p, NuGetVersion.Parse("1.0.0"))).ToList(), pins, new Dictionary<string, NuGetVersion>(), _ => false),
            ct);

        Assert.Empty(resolution.Errors);
        Assert.Empty(resolution.Incompatibilities);
        return resolution.Packages.Values.Select(p => $"{p.Id}/{p.Version.ToNormalizedString()}").Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task<IReadOnlyList<string>> RestoreAsync(string name, string[] plugins, Dictionary<string, NuGetVersion> pins, string[] sources, CancellationToken ct)
    {
        var workspace = fixtures.Workspace;
        var directory = Path.Combine(workspace.RootDirectory, $"oracle-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var references = plugins.Select(p => (Id: p, Version: "1.0.0")).Concat(pins.Select(p => (Id: p.Key, Version: p.Value.ToNormalizedString())));
        await File.WriteAllTextAsync(Path.Combine(directory, "oracle.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <NuGetAudit>false</NuGetAudit>
                <!-- NU1608 (outside a dependency's range) would only be a warning; restore's resolved set is what counts. -->
                <NoWarn>$(NoWarn);NU1608</NoWarn>
              </PropertyGroup>
              <ItemGroup>
            {string.Join(Environment.NewLine, references.Select(r => $"    <PackageReference Include=\"{r.Id}\" Version=\"[{r.Version}]\" />"))}
              </ItemGroup>
            </Project>
            """, ct);
        var config = Path.Combine(directory, "nuget.config");
        await File.WriteAllTextAsync(config, $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
            {string.Join(Environment.NewLine, sources.Select((s, i) => $"    <add key=\"source{i}\" value=\"{SecurityElement.Escape(s)}\" />"))}
              </packageSources>
            </configuration>
            """, ct);

        (await workspace.RunAsync(["restore", directory, "--configfile", config, "--disable-build-servers"], ct)).EnsureSuccess();

        using var assets = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(directory, "obj", "project.assets.json"), ct));
        return assets.RootElement.GetProperty("libraries").EnumerateObject()
            .Where(l => l.Value.GetProperty("type").GetString() == "package")
            .Select(l => l.Name)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

using System.Diagnostics;
using Alcove.Internal.Hosting;
using Alcove.IntegrationTests.Infrastructure;
using Fixture.HostContracts;

namespace Alcove.IntegrationTests.Performance;

/// <summary>
/// The implementation plan's Phase 8 performance check: startup (<see cref="PluginHost.Create"/> + <see cref="PluginHost.LoadAsync"/>)
/// with 50 small plugins, and one hot install into the loaded host. Generates and packs 51 plugin projects (about a
/// minute), so it only runs on request:
/// <c>ALCOVE_PERF_TESTS=1 dotnet test tests/Alcove.IntegrationTests --filter Category=Performance --logger "console;verbosity=detailed"</c>.
/// The timings go to the test output; the budget assertions are loose, to catch regressions of an order of magnitude.
/// </summary>
[Trait("Category", "Performance")]
public sealed class StartupPerformanceTests : IDisposable
{
    private const int PluginCount = 50;
    private const string PackageType = "FixturePlugin";

    private readonly PluginRootBuilder _root = new();
    private readonly List<PluginHost> _hosts = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (var host in _hosts)
        {
            host.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        _root.Dispose();
    }

    [Fact]
    public async Task FiftyPlugins_StartQuickly_AndAHotInstallIsQuick()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("ALCOVE_PERF_TESTS") == "1",
            "Performance check: set ALCOVE_PERF_TESTS=1 to run it (it packs 51 generated plugins).");
        var output = TestContext.Current.TestOutputHelper!;

        using var workspace = FixtureWorkspace.Create();
        var packages = await PackPluginsAsync(workspace);
        var ids = Enumerable.Range(1, PluginCount).Select(PluginId).ToArray();
        var inventory = HostInventory.FromCurrentProcess([HostingFixtures.ContractsPackage]);

        var install = Stopwatch.StartNew();
        await _root.InstallAsync(inventory, Directory.GetFiles(packages, "*.nupkg"), ids);
        output.WriteLine($"Installed {PluginCount} plugins without a feed in {install.ElapsedMilliseconds} ms");

        // Cold: the first host in this test (Alcove's own code may already be JIT-compiled by other tests).
        // Warm: the same root again with new hosts (new cells, so the plugins' assemblies load again).
        var startups = new List<(long Create, long Load)>();
        PluginHost? host = null;
        for (var run = 0; run < 3; run++)
        {
            var create = Stopwatch.StartNew();
            host = Create(packages);
            create.Stop();
            var load = Stopwatch.StartNew();
            await host.LoadAsync(Ct);
            load.Stop();

            Assert.True(PluginCount == host.Plugins.Count(p => p.Status == PluginStatus.Loaded),
                string.Join(Environment.NewLine, host.Plugins.Take(3)) + Environment.NewLine + await host.ValidateAsync(Ct));
            Assert.Equal(PluginCount, host.Catalog.GetAll<IDescriber>().Count);
            startups.Add((create.ElapsedMilliseconds, load.ElapsedMilliseconds));
            output.WriteLine($"Startup {run + 1} ({(run == 0 ? "cold" : "warm")}): Create {create.ElapsedMilliseconds} ms, LoadAsync {load.ElapsedMilliseconds} ms");
        }

        var validate = Stopwatch.StartNew();
        var report = await host!.ValidateAsync(Ct);
        output.WriteLine($"ValidateAsync alone: {validate.ElapsedMilliseconds} ms ({report.Issues.Count} issue(s))");
        Assert.Empty(report.Issues);

        var extensions = Stopwatch.StartNew();
        var described = host.Catalog.GetAll<IDescriber>().Select(e => e.Value.Describe()).ToList();
        output.WriteLine($"Created and called {described.Count} extensions: {extensions.ElapsedMilliseconds} ms");

        var hot = Stopwatch.StartNew();
        var result = await host.InstallAsync(PluginId(PluginCount + 1), Ct);
        hot.Stop();
        Assert.True(result.Status == InstallStatus.Loaded, result.ToString());
        output.WriteLine($"Hot InstallAsync (folder feed) into the loaded host: {hot.ElapsedMilliseconds} ms");

        var verify = Stopwatch.StartNew();
        var verification = await host.VerifyAsync(Ct);
        output.WriteLine($"VerifyAsync (full re-hash): {verify.ElapsedMilliseconds} ms, {verification.PackagesChecked} packages, {verification.FilesChecked} files");

        // Budgets an order of magnitude above this machine's numbers (see the Phase 8 report).
        Assert.True(startups.Min(s => s.Create + s.Load) < 5_000, "Startup with 50 plugins took more than 5 s.");
        Assert.True(hot.ElapsedMilliseconds < 10_000, "A hot install took more than 10 s.");
    }

    private PluginHost Create(string feed)
    {
        var host = PluginHost.Create(new PluginHostOptions
        {
            Root = _root.Root,
            PluginPackageType = PackageType,
            HostOwnedPackages = { HostingFixtures.ContractsPackage },
            Feeds = { new PackageFeed(feed) },
        });
        _hosts.Add(host);
        return host;
    }

    private static string PluginId(int number) => $"Perf.Plugin{number:D2}";

    /// <summary>
    /// Generates a shared library and 51 plugins (each with an entry point registering one extension, one
    /// attribute-discovered extension, and a dependency on the library, so every cell gets a private copy), packed with
    /// Alcove.Sdk against Fixture.HostContracts. Returns the folder holding the packages.
    /// </summary>
    private static async Task<string> PackPluginsAsync(FixtureWorkspace workspace)
    {
        workspace.AddToFeed(TestEnvironment.AlcovePackage(HostingFixtures.ContractsPackage));
        var directory = Path.Combine(workspace.FixturesDirectory, "Perf");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "Directory.Build.props"), """
            <Project>
              <Import Project="$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))" />
              <PropertyGroup>
                <IsPackable>true</IsPackable>
                <Version>1.0.0</Version>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(directory, "Directory.Build.targets"), """
            <Project>
              <Import Project="$([MSBuild]::GetPathOfFileAbove('Directory.Build.targets', '$(MSBuildThisFileDirectory)../'))" />
            </Project>
            """);

        var projects = new List<string> { WriteProject(directory, "Perf.Common", "", """
            namespace Perf.Common;

            public static class Text
            {
                public static string Describe(string plugin) => $"{plugin} (Perf.Common {typeof(Text).Assembly.GetName().Version})";
            }
            """) };

        for (var number = 1; number <= PluginCount + 1; number++)
        {
            var id = PluginId(number);
            projects.Add(WriteProject(directory, id, $$"""
                  <PropertyGroup>
                    <PluginEntryPoint>{{id}}.Plugin</PluginEntryPoint>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="Alcove.Sdk" Version="$(AlcoveVersion)" PrivateAssets="all" />
                    <PackageReference Include="Fixture.HostContracts" Version="$(AlcoveVersion)" />
                    <ProjectReference Include="../Perf.Common/Perf.Common.csproj" />
                  </ItemGroup>
                """, $$"""
                using Alcove;
                using Fixture.HostContracts;

                namespace {{id}};

                public sealed class Plugin : IPlugin
                {
                    public void Register(IExtensionRegistrar registrar, IPluginContext context)
                        => registrar.Add<IDescriber>(() => new Describer());
                }

                public sealed class Describer : IDescriber
                {
                    public string Describe() => Perf.Common.Text.Describe("{{id}}");
                }

                [Greeter(Language = "{{id}}")]
                public sealed class Greeter : IGreeter
                {
                    public string Greet(string name) => $"{{id}} greets {name}";
                }
                """));
        }

        File.WriteAllText(Path.Combine(directory, "Perf.slnx"),
            "<Solution>\n" + string.Concat(projects.Select(p => $"  <Project Path=\"{p}\" />\n")) + "</Solution>\n");

        var packages = Path.Combine(workspace.RootDirectory, "perf-packages");
        var result = await workspace.RunAsync(
            [
                "pack", Path.Combine(directory, "Perf.slnx"), "--output", packages, "--disable-build-servers",
                $"-p:AlcoveVersion={TestEnvironment.AlcoveVersion}", $"-p:RestoreConfigFile={workspace.NuGetConfigPath}",
            ],
            Ct);
        result.EnsureSuccess();
        return packages;
    }

    private static string WriteProject(string directory, string name, string items, string code)
    {
        Directory.CreateDirectory(Path.Combine(directory, name));
        File.WriteAllText(Path.Combine(directory, name, name + ".csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
            {items}
            </Project>
            """);
        File.WriteAllText(Path.Combine(directory, name, "Code.cs"), code);
        return $"{name}/{name}.csproj";
    }
}

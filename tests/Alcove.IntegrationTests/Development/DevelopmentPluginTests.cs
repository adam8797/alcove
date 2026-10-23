using System.Runtime.Loader;
using Alcove.Development;
using Alcove.Feeds;
using Alcove.Hosting;
using Alcove.Install;
using Alcove.IntegrationTests.Infrastructure;
using Alcove.Packaging;
using Alcove.Validation;
using Fixture.HostContracts;

namespace Alcove.IntegrationTests.Development;

/// <summary>
/// Development plugins (ADR 0017) end to end: the Hosting fixtures' real build outputs (the <c>bin/Release</c> folders
/// their packing left, with the manifest and dependencies Alcove.Sdk puts there), loaded in place next to plugins
/// installed in a temporary plugin root.
/// </summary>
public sealed class DevelopmentPluginTests(HostingFixtures fixtures) : IDisposable
{
    private const string UserA = "Fixture.Hosting.UserA";
    private const string UserB = "Fixture.Hosting.UserB";
    private const string Provider = "Fixture.Hosting.Provider";
    private const string Consumer = "Fixture.Hosting.Consumer";

    private static readonly Lazy<HostInventory> s_inventory = new(() => HostInventory.FromCurrentProcess([HostingFixtures.ContractsPackage]));

    private readonly PluginRootBuilder _root = new();
    private readonly TempDirectory _host = new("alcove-dev-host");
    private readonly ListLoggerFactory _logs = new();
    private readonly List<PluginHost> _hosts = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (var host in _hosts)
        {
            host.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        _root.Dispose();
        _host.Dispose();
    }

    [Fact]
    public async Task ABuildOutput_LoadsInPlace_AlongsideInstalledPlugins()
    {
        await _root.InstallAsync(s_inventory.Value, fixtures.Packages, UserA);
        var host = Create(BuildOutput(UserB));

        Assert.Empty((await host.ValidateAsync(Ct)).Issues);
        await host.LoadAsync(Ct);

        Assert.All(host.Plugins, p => Assert.Equal(PluginStatus.Loaded, p.Status));
        Assert.False(Plugin(host, UserA).IsDevelopment);
        Assert.True(Plugin(host, UserB).IsDevelopment);
        Assert.Equal(UserB, Plugin(host, UserB).Cell);
        Assert.Equal("UserA uses Dep 1.0 in 'Fixture.Hosting.UserA'", Describe(host, UserA));
        Assert.Equal("UserB uses Dep 2.0 in 'Fixture.Hosting.UserB' and NewIn2 present; services: False", Describe(host, UserB));

        // Its assemblies come straight from bin/, into its own cell: nothing was copied.
        var cell = host.CellEnvironment.FindCell(UserB)!;
        Assert.All(cell.Assemblies, a => Assert.StartsWith(BuildOutput(UserB), a.Location, StringComparison.Ordinal));
        Assert.Contains(cell.Assemblies, a => a.GetName().Name == "Fixture.Dep" && a.GetName().Version == new Version(2, 0, 0, 0));
        Assert.Null(AssemblyLoadContext.Default.Assemblies.FirstOrDefault(a => a.GetName().Name == UserB));
    }

    [Fact]
    public async Task ADevelopmentPlugin_WithAnInstalledPluginsId_LoadsInsteadOfIt_WithAWarning()
    {
        await _root.InstallAsync(s_inventory.Value, fixtures.Packages, Consumer);   // and Provider, its plugin dependency
        var stateBefore = File.ReadAllBytes(StateFile);
        var host = Create(BuildOutput(Provider));

        await host.LoadAsync(Ct);

        var warning = Assert.Single((await host.ValidateAsync(Ct)).Issues);
        Assert.Equal((ValidationSeverity.Warning, ValidationCodes.DevelopmentPluginOverridesInstalled, Provider), (warning.Severity, warning.Code, warning.PluginId));
        Assert.Equal([Provider, Consumer], host.Plugins.Select(p => p.Id));
        Assert.True(Plugin(host, Provider).IsDevelopment);
        Assert.False(Plugin(host, Consumer).IsDevelopment);

        // The installed consumer uses the development provider across cells, exposed dependency included.
        Assert.Equal(
            "Consumer: IS my Widget: Widget(Dep 2.0 in 'Fixture.Hosting.Provider'); ProviderService in 'Fixture.Hosting.Provider'",
            Describe(host, Consumer));
        Assert.StartsWith(BuildOutput(Provider), host.CellEnvironment.FindCell(Provider)!.Assemblies.Single(a => a.GetName().Name == Provider).Location, StringComparison.Ordinal);

        // The installed provider is untouched, and nothing is pending.
        Assert.Equal(stateBefore, File.ReadAllBytes(StateFile));
        Assert.Empty(await host.GetPendingChangesAsync(Ct));
    }

    [Fact]
    public async Task ADevelopmentPlugin_DependsOnAnInstalledPlugin_AcrossCells()
    {
        await _root.InstallAsync(s_inventory.Value, fixtures.Packages, Provider);
        var host = Create(BuildOutput(Consumer));

        Assert.Empty((await host.ValidateAsync(Ct)).Issues);
        await host.LoadAsync(Ct);

        Assert.All(host.Plugins, p => Assert.Equal(PluginStatus.Loaded, p.Status));
        Assert.Equal(
            "Consumer: IS my Widget: Widget(Dep 2.0 in 'Fixture.Hosting.Provider'); ProviderService in 'Fixture.Hosting.Provider'",
            Describe(host, Consumer));

        // Its bin/ holds copies of the provider and Dep, but they come from the installed provider's cell.
        Assert.True(File.Exists(Path.Combine(BuildOutput(Consumer), $"{Provider}.dll")));
        var consumerCell = host.CellEnvironment.FindCell(Consumer)!;
        Assert.DoesNotContain(consumerCell.Assemblies, a => a.GetName().Name is Provider or "Fixture.Dep");
    }

    [Fact]
    public async Task TwoDevelopmentPlugins_CanDependOnEachOther()
    {
        var host = Create(BuildOutput(Consumer), BuildOutput(Provider));

        Assert.Empty((await host.ValidateAsync(Ct)).Issues);
        await host.LoadAsync(Ct);

        Assert.Equal([Consumer, Provider], host.Plugins.Where(p => p.Status == PluginStatus.Loaded).Select(p => p.Id).Order());
        Assert.All(host.Plugins, p => Assert.True(p.IsDevelopment));
        Assert.StartsWith("Consumer: IS my Widget", Describe(host, Consumer), StringComparison.Ordinal);
    }

    [Fact]
    public async Task APluginDependencyThatIsNeitherInstalledNorADevelopmentPlugin_IsMissing()
    {
        // Consumer's bin/ holds a copy of the Provider plugin, which its deps.json lists like any library. The manifest
        // the SDK wrote there says it's a plugin, so as an install without Provider would, Consumer doesn't load.
        var manifest = PluginManifest.Parse(File.ReadAllText(Path.Combine(BuildOutput(Consumer), PluginManifest.GetFileName(Consumer))));
        Assert.Equal([Provider], manifest.PluginDependencies);
        var host = Create(BuildOutput(Consumer));

        var report = await host.ValidateAsync(Ct);
        await host.LoadAsync(Ct);

        var error = Assert.Single(report.Errors);
        Assert.Equal((ValidationCodes.PluginDependencyMissing, Consumer), (error.Code, error.PluginId));
        Assert.Contains(Provider, error.Message, StringComparison.Ordinal);
        Assert.Equal(PluginStatus.NotLoaded, Plugin(host, Consumer).Status);
        Assert.Null(host.CellEnvironment.FindCell(Consumer));
    }

    [Fact]
    public async Task AHostOwnedConflict_IsAnError()
    {
        // A host whose host-owned Fixture.Dep is 1.0: UserB was built against 2.0, so an install would be incompatible too.
        var environment = FabricatedHost.With(_host, ("Fixture.Dep", "1.0.0", "1.0.0.0"));
        var host = Create(environment, ["Fixture.Dep"], BuildOutput(UserB));

        var report = await host.ValidateAsync(Ct);
        await host.LoadAsync(Ct);

        Assert.Contains(report.Errors, e => e.Code == ValidationCodes.HostOwnedConflict && e.PluginId == UserB);
        Assert.Equal(PluginStatus.NotLoaded, Plugin(host, UserB).Status);
        Assert.Contains("Fixture.Dep", Plugin(host, UserB).Reason);
        Assert.Null(host.CellEnvironment.FindCell(UserB));
    }

    [Fact]
    public async Task ADependencyResolvedDifferentlyThanAnInstallWould_IsAWarning()
    {
        // The host ships Fixture.Dep 3.0 (host-shared): like an install, UserB gets the host's copy, not its bin/'s 2.0.
        var environment = FabricatedHost.With(_host, ("Fixture.Dep", "3.0.0", "3.0.0.0"));
        var host = Create(environment, [], BuildOutput(UserB));

        var report = await host.ValidateAsync(Ct);

        var warning = Assert.Single(report.Issues);
        Assert.Equal((ValidationSeverity.Warning, ValidationCodes.DevelopmentDependencyDiffers, UserB), (warning.Severity, warning.Code, warning.PluginId));
        Assert.Contains("Fixture.Dep 2.0.0", warning.Message);
        Assert.Contains("3.0.0", warning.Message);
    }

    [Fact]
    public void AFileReferenceToAHostSharedAssembly_DoesntMakeItCellPrivate()
    {
        // Consumer's deps.json lists Fixture.Dep twice: the project (2.0.0) and a "reference" library
        // (Fixture.Dep.Reference/2.0.0.0, from DisableTransitiveProjectReferences). The host ships Dep 3.0, so like an
        // install the plugin takes the host's copy; the file reference must not bring the bin/ copy back. (Consumer uses
        // Dep through its plugin dependency Provider's API, so in the cells Dep resolves through Provider's.)
        var environment = FabricatedHost.With(_host, ("Fixture.Dep", "3.0.0", "3.0.0.0"));
        var inventory = HostInventory.Create(environment, [HostingFixtures.ContractsPackage]);
        var validator = new PluginValidator(
            inventory, _root.Layout, [], development: [new DevelopmentPlugin(BuildOutput(Consumer)), new DevelopmentPlugin(BuildOutput(Provider))]);

        var plan = validator.Validate(Ct);

        Assert.Contains("Fixture.Dep.Reference", File.ReadAllText(Path.Combine(BuildOutput(Consumer), $"{Consumer}.deps.json")), StringComparison.Ordinal);
        Assert.Contains(plan.Report.Warnings, w => w.Code == ValidationCodes.DevelopmentDependencyDiffers && w.PluginId == Consumer);
        Assert.False(plan.Cells.Single(c => c.Cell == Consumer).Plan.Private.ContainsKey("Fixture.Dep"));
        Assert.Contains("Fixture.Dep", plan.Cells.Single(c => c.Cell == Provider).Plan.HostShared);
        Assert.Empty(plan.Report.Errors);
    }

    [Fact]
    public async Task NothingIsWrittenToThePluginRoot()
    {
        // An empty root: loading a development plugin creates no state file.
        var host = Create(BuildOutput(UserB));
        await host.LoadAsync(Ct);

        Assert.Equal(PluginStatus.Loaded, Plugin(host, UserB).Status);
        Assert.False(File.Exists(StateFile));
        Assert.Empty(await host.GetPendingChangesAsync(Ct));
        await host.DisposeAsync();

        // With installed plugins: the state file is unchanged, and no file Alcove keeps in the root names the development
        // plugin (its data directory is the plugin's own, as for an installed plugin).
        await _root.InstallAsync(s_inventory.Value, fixtures.Packages, UserA);
        var stateBefore = File.ReadAllBytes(StateFile);
        var stamp = File.GetLastWriteTimeUtc(StateFile);
        host = Create(BuildOutput(UserB));
        await host.LoadAsync(Ct);

        Assert.All(host.Plugins, p => Assert.Equal(PluginStatus.Loaded, p.Status));
        Assert.Equal(stateBefore, File.ReadAllBytes(StateFile));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(StateFile));
        var data = Path.Combine(_root.Root, "data") + Path.DirectorySeparatorChar;
        foreach (var file in Directory.EnumerateFiles(_root.Root, "*", SearchOption.AllDirectories).Where(f => !f.StartsWith(data, StringComparison.Ordinal)))
        {
            Assert.DoesNotContain(UserB, file, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(UserB, File.ReadAllText(file), StringComparison.OrdinalIgnoreCase);
        }

        Assert.Empty(await host.GetPendingChangesAsync(Ct));
    }

    [Fact]
    public async Task InstallingAPluginWithADevelopmentPluginsId_IsPendingRestart()
    {
        var host = Create(BuildOutput(UserB));
        host.Verifier = new AllowAllVerifier();
        await host.LoadAsync(Ct);

        var result = await host.InstallAsync(UserB, Ct);

        Assert.Equal(InstallStatus.PendingRestart, result.Status);
        Assert.Contains(result.Reasons, r => r.Contains("development plugin", StringComparison.Ordinal));
        var running = Assert.Single(host.Plugins);
        Assert.True(running.IsDevelopment);
        Assert.Equal(PluginStatus.Loaded, running.Status);
        Assert.True(File.Exists(StateFile));   // the install itself is recorded, for the next startup
    }

    [Fact]
    public async Task AnUnusableFolder_IsAPluginError_TheOthersLoad()
    {
        await _root.InstallAsync(s_inventory.Value, fixtures.Packages, UserA);
        var missing = _host.Combine("no-such-plugin", "bin");
        var noManifest = Directory.CreateDirectory(_host.Combine("not-a-plugin")).FullName;
        var host = Create(missing, noManifest);

        var report = await host.ValidateAsync(Ct);
        await host.LoadAsync(Ct);

        Assert.Equal(2, report.Errors.Count(e => e.Code == ValidationCodes.DevelopmentPluginInvalid && !e.IsGlobal));
        Assert.Contains(report.Errors, e => e.Message.Contains("doesn't exist", StringComparison.Ordinal));
        Assert.Contains(report.Errors, e => e.Message.Contains("no plugin manifest", StringComparison.Ordinal));
        Assert.Equal([UserA], host.Plugins.Select(p => p.Id));
        Assert.Equal(PluginStatus.Loaded, Plugin(host, UserA).Status);
    }

    [Fact]
    public async Task ABuildOutputMissingAFile_IsListedAsNotLoaded()
    {
        var copy = _host.Combine("userb-bin");
        TempDirectory.CopyTree(BuildOutput(UserB), copy);
        File.Delete(Path.Combine(copy, "Fixture.Dep.dll"));
        var host = Create(copy);

        await host.LoadAsync(Ct);

        var plugin = Plugin(host, UserB);
        Assert.True(plugin.IsDevelopment);
        Assert.Equal(PluginStatus.NotLoaded, plugin.Status);
        Assert.Contains("Fixture.Dep.dll", plugin.Reason);
    }

    private string StateFile => Path.Combine(_root.Root, "state.json");

    /// <summary>The fixture's build output, as its packing (Release) left it.</summary>
    private string BuildOutput(string fixture)
    {
        var path = Path.Combine(fixtures.Workspace.FixturesDirectory, "Hosting", fixture, "bin", "Release", "net10.0");
        Assert.True(File.Exists(Path.Combine(path, $"{fixture}.plugin.json")), $"Alcove.Sdk didn't write the manifest into '{path}'.");
        return path;
    }

    private PluginHost Create(params string[] developmentPlugins) => Create(null, [], developmentPlugins);

    private PluginHost Create(HostEnvironment? environment, string[] hostOwned, params string[] developmentPlugins)
    {
        var options = new PluginHostOptions
        {
            Root = _root.Root,
            Feeds = { new PackageFeed(fixtures.PackagesDirectory) },
            HostOwnedPackages = { HostingFixtures.ContractsPackage },
            LoggerFactory = _logs,
        };
        foreach (var id in hostOwned)
        {
            options.HostOwnedPackages.Add(id);
        }

        foreach (var path in developmentPlugins)
        {
            options.DevelopmentPlugins.Add(new DevelopmentPlugin(path));
        }

        var host = environment is null ? PluginHost.Create(options) : PluginHost.Create(options, environment);
        _hosts.Add(host);
        return host;
    }

    private static PluginInfo Plugin(PluginHost host, string id) => host.Plugins.Single(p => p.Id == id);

    private static string Describe(PluginHost host, string pluginId) =>
        host.Catalog.GetAll<IDescriber>().Single(e => e.Plugin.Id == pluginId).Value.Describe();
}

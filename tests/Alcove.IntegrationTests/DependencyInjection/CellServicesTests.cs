using System.Runtime.Loader;
using Alcove.Internal.Hosting;
using Alcove.IntegrationTests.Infrastructure;
using Fixture.HostContracts;

namespace Alcove.IntegrationTests.DependencyInjection;

/// <summary>The DI fixture packages, packed once for both adapters' test classes.</summary>
[CollectionDefinition(Name)]
public sealed class DependencyInjectionCollection : ICollectionFixture<DependencyInjectionFixtures>
{
    public const string Name = "Dependency injection add-ons";
}

/// <summary>
/// The shared test suite of the dependency injection add-ons (implementation plan, Phase 6), run against each adapter
/// with real plugin packages (tests/fixtures/DependencyInjection): extension constructors receive host and cell
/// services under both containers, including for hot-installed plugins.
/// </summary>
/// <remarks>
/// The app's container holds an <see cref="IEventLog"/> and an <see cref="ICellService"/> named "Host". The service
/// plugins register an <see cref="ICellService"/> named after themselves and the adapter, e.g. "Alpha (ms)".
/// </remarks>
public abstract class CellServicesTests(DependencyInjectionFixtures fixtures) : IAsyncLifetime
{
    protected const string Alpha = "Fixture.DI.Alpha";
    protected const string Beta = "Fixture.DI.Beta";
    protected const string Plain = "Fixture.DI.Plain";
    private const string PackageType = "FixturePlugin";

    private readonly TempDirectory _root = new("alcove-di");
    private readonly List<IAsyncDisposable> _containers = [];

    /// <summary>The app's container of the last <see cref="LoadAsync"/>.</summary>
    private IAppContainer? _container;

    /// <summary>The add-on's package, which it makes host-owned.</summary>
    protected abstract string AdapterPackage { get; }

    /// <summary>The adapter's name in the fixtures' service names: <c>ms</c> or <c>autofac</c>.</summary>
    protected abstract string AdapterName { get; }

    protected EventLog Log { get; } = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Builds the app's container: <see cref="Log"/> as <see cref="IEventLog"/>, a host <see cref="ICellService"/>
    /// and the add-on's <c>AddPlugins(configure)</c>.
    /// </summary>
    protected abstract IAppContainer BuildContainer(Action<PluginHostOptions> configure);

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var container in _containers)
        {
            await container.DisposeAsync();
        }

        _root.DisposeBestEffort();   // cells keep the root's assemblies loaded
    }

    [Fact]
    public async Task Extensions_GetHostAndCellServicesThroughTheirConstructors()
    {
        var host = await InstallAndLoadAsync([Alpha]);

        AssertLoaded(host, Alpha);
        Assert.Equal($"Alpha sees Alpha ({AdapterName}), Host", Describe(host, Alpha));
    }

    [Fact]
    public async Task PluginContextServices_ResolveTheCellsServices_BeforeTheHosts()
    {
        await InstallAndLoadAsync([Alpha]);

        Assert.Contains($"Alpha Register: formatter True, cell service Alpha ({AdapterName})", Log.Entries);
    }

    [Fact]
    public async Task PluginsSharingACell_SeeEachOthersServices()
    {
        var host = await InstallAndLoadAsync([Alpha, Beta], o => o.Cells.Add(new CellDefinition("Shared") { Plugins = { Alpha, Beta } }));

        AssertLoaded(host, Alpha, Beta);
        Assert.Equal($"Alpha sees Alpha ({AdapterName}), Beta ({AdapterName}), Host", Describe(host, Alpha));
        Assert.Equal($"Beta sees Alpha ({AdapterName}), Beta ({AdapterName}), Host", Describe(host, Beta));
    }

    [Fact]
    public async Task PluginsInDifferentCells_DontSeeEachOthersServices()
    {
        var host = await InstallAndLoadAsync([Alpha, Beta]);

        AssertLoaded(host, Alpha, Beta);
        Assert.NotEqual(Plugin(host, Alpha).Cell, Plugin(host, Beta).Cell);
        Assert.Equal($"Alpha sees Alpha ({AdapterName}), Host", Describe(host, Alpha));
        Assert.Equal($"Beta sees Beta ({AdapterName}), Host", Describe(host, Beta));
    }

    [Fact]
    public async Task SingletonAndTransientExtensions_ShareTheCellsSingletonDependency()
    {
        var host = await InstallAndLoadAsync([Alpha]);

        var describer = host.Catalog.GetAll<IDescriber>().Single();
        Assert.Same(describer.Value, describer.Value);

        var greeter = host.Catalog.GetAll<IGreeter>().Single();
        Assert.Equal(ExtensionLifetime.Transient, greeter.Lifetime);
        var first = greeter.CreateInstance().Greet("x").Split('/');
        var second = greeter.CreateInstance().Greet("x").Split('/');
        Assert.NotEqual(first[0], second[0]);   // a new extension each time
        Assert.Equal(first[1], second[1]);      // the cell's one formatter
    }

    [Fact]
    public async Task APluginWithoutServices_GetsConstructorInjectionFromTheHost()
    {
        var host = await InstallAndLoadAsync([Plain]);

        AssertLoaded(host, Plain);
        Assert.Equal("Plain sees Host", Describe(host, Plain));
        Assert.Contains("Plain described", Log.Entries);
    }

    [Fact]
    public async Task APluginWithoutServices_InACellWithServices_SeesThem()
    {
        var host = await InstallAndLoadAsync([Alpha, Plain], o => o.Cells.Add(new CellDefinition("Shared") { Plugins = { Alpha, Plain } }));

        AssertLoaded(host, Alpha, Plain);
        Assert.Equal($"Plain sees Alpha ({AdapterName}), Host", Describe(host, Plain));
    }

    [Fact]
    public async Task Extensions_AreNeverRegisteredInTheAppsContainer()
    {
        var host = await InstallAndLoadAsync([Alpha, Plain]);

        Assert.Equal(2, host.Catalog.GetAll<IDescriber>().Count);
        Assert.Empty(_container!.ResolveAll<IDescriber>());
        Assert.Same(host.Catalog, _container.Resolve<ExtensionCatalog>());
    }

    [Fact]
    public async Task DisposingTheContainer_DisposesExtensionsThenPluginsThenCellServices_InReverseLoadOrder()
    {
        var host = await InstallAndLoadAsync([Alpha, Beta]);
        Assert.Equal([Alpha, Beta], host.Plugins.Select(p => p.Id));
        _ = Describe(host, Alpha);
        _ = Describe(host, Beta);

        await _container!.DisposeAsync();

        Assert.Equal(
            ["Beta extension disposed", "Beta plugin disposed", "Beta cell service disposed",
             "Alpha extension disposed", "Alpha plugin disposed", "Alpha cell service disposed"],
            Log.Entries.Where(e => e.EndsWith(" disposed", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task DisposingTheContainer_DisposesASharedCellsServicesAfterAllItsPlugins()
    {
        var host = await InstallAndLoadAsync([Alpha, Beta], o => o.Cells.Add(new CellDefinition("Shared") { Plugins = { Alpha, Beta } }));
        Assert.Equal([Alpha, Beta], host.Plugins.Select(p => p.Id));
        _ = Describe(host, Alpha);
        _ = Describe(host, Beta);

        await _container!.DisposeAsync();

        var disposed = Log.Entries.Where(e => e.EndsWith(" disposed", StringComparison.Ordinal)).ToList();
        Assert.Equal(["Beta extension disposed", "Beta plugin disposed", "Alpha extension disposed", "Alpha plugin disposed"], disposed.Take(4));
        Assert.Equal(["Alpha cell service disposed", "Beta cell service disposed"], disposed.Skip(4).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task AHotInstallIntoANewCell_CreatesTheCellsServices()
    {
        var host = await LoadAsync(withFeed: true);

        var result = await host.InstallAsync(Alpha, cancellationToken: Ct);

        Assert.True(result.Status == InstallStatus.Loaded, result.ToString());
        Assert.Equal($"Alpha sees Alpha ({AdapterName}), Host", Describe(host, Alpha));
        Assert.Contains($"Alpha Register: formatter True, cell service Alpha ({AdapterName})", Log.Entries);
    }

    [Fact]
    public async Task AHotInstallOfAServicePluginIntoACellWithServices_IsPendingRestart_WhileAPluginWithoutServicesLoads()
    {
        void Shared(PluginHostOptions o) => o.Cells.Add(new CellDefinition("Shared") { Plugins = { Alpha, Beta, Plain } });
        await InstallAsync([Alpha]);
        var host = await LoadAsync(Shared, withFeed: true);
        Assert.Equal($"Alpha sees Alpha ({AdapterName}), Host", Describe(host, Alpha));

        var beta = await host.InstallAsync(Beta, cancellationToken: Ct);

        Assert.True(beta.Status == InstallStatus.PendingRestart, beta.ToString());
        Assert.Contains($"{Beta} registers services, but cell 'Shared' already has its service provider", beta.Reason);
        Assert.Equal(PluginStatus.NotLoaded, Plugin(host, Beta).Status);
        Assert.DoesNotContain(host.Catalog.GetAll<IDescriber>(), e => e.Plugin.Id == Beta);

        var plain = await host.InstallAsync(Plain, cancellationToken: Ct);

        Assert.True(plain.Status == InstallStatus.Loaded, plain.ToString());
        Assert.Equal($"Plain sees Alpha ({AdapterName}), Host", Describe(host, Plain));

        // At the next startup the cell's services hold Beta's registrations too.
        var restarted = await LoadAsync(Shared);
        AssertLoaded(restarted, Alpha, Beta, Plain);
        Assert.Equal($"Beta sees Alpha ({AdapterName}), Beta ({AdapterName}), Host", Describe(restarted, Beta));
        Assert.Equal($"Plain sees Alpha ({AdapterName}), Beta ({AdapterName}), Host", Describe(restarted, Plain));
    }

    [Fact]
    public async Task AServicesCausedPendingRestart_LeavesTheRunningCellUntouched_AndLoadsNothing()
    {
        await InstallAsync([Alpha]);
        var host = await LoadAsync(o => o.Cells.Add(new CellDefinition("Shared") { Plugins = { Alpha, Beta } }), withFeed: true);
        var cell = host.CellEnvironment.FindCell("Shared")!;
        var before = Snapshot(cell);

        var beta = await host.InstallAsync(Beta, cancellationToken: Ct);

        Assert.True(beta.Status == InstallStatus.PendingRestart, beta.ToString());
        Assert.Empty(beta.ConflictingNames);
        Assert.Equal(before, Snapshot(cell));
        Assert.DoesNotContain(cell.GetCommittedAnswers().Assemblies.Keys, name => name.StartsWith(Beta, StringComparison.OrdinalIgnoreCase));

        // Cells load by path from the root: nothing of Beta's package is loaded anywhere, from this test's root.
        var loaded = AssemblyLoadContext.All.SelectMany(a => a.Assemblies)
            .Where(a => !a.IsDynamic && a.Location.StartsWith(_root.Path, StringComparison.Ordinal))
            .Select(a => a.GetName().Name)
            .ToList();
        Assert.Contains(Alpha, loaded);
        Assert.DoesNotContain(Beta, loaded);

        static List<string> Snapshot(Alcove.Internal.Cells.Cell cell)
        {
            var committed = cell.GetCommittedAnswers();
            return committed.Assemblies.Concat(committed.NativeLibraries)
                .Select(a => $"{a.Key}: {a.Value}")
                .Order(StringComparer.Ordinal)
                .ToList();
        }
    }

    private async Task<PluginHost> InstallAndLoadAsync(string[] plugins, Action<PluginHostOptions>? configure = null)
    {
        await InstallAsync(plugins);
        return await LoadAsync(configure);
    }

    /// <summary>Installs without a feed, classifying as a host using the adapter does.</summary>
    private async Task InstallAsync(string[] plugins)
    {
        var inventory = HostInventory.FromCurrentProcess([HostingFixtures.ContractsPackage, AdapterPackage]);
        await new Alcove.Internal.Install.LocalPackageInstaller(inventory, _root.Path).InstallAsync(fixtures.Packages, plugins, Ct);
    }

    private async Task<PluginHost> LoadAsync(Action<PluginHostOptions>? configure = null, bool withFeed = false)
    {
        PluginHostOptions? configured = null;
        _container = BuildContainer(options =>
        {
            configured = options;
            options.Root = _root.Path;
            options.PluginPackageType = PackageType;
            options.HostOwnedPackages.Add(HostingFixtures.ContractsPackage);
            if (withFeed)
            {
                options.Feeds.Add(new PackageFeed(fixtures.PackagesDirectory));
            }

            configure?.Invoke(options);
        });
        _containers.Add(_container);
        var host = _container.Resolve<PluginHost>();

        // AddPlugins made the add-on host-owned without the app listing it.
        Assert.Contains(AdapterPackage, configured!.HostOwnedPackages);
        await host.LoadAsync(Ct);
        return host;
    }

    private static PluginInfo Plugin(PluginHost host, string id) => host.Plugins.Single(p => p.Id == id);

    private static void AssertLoaded(PluginHost host, params string[] ids)
    {
        foreach (var id in ids)
        {
            var plugin = Plugin(host, id);
            Assert.True(plugin.Status == PluginStatus.Loaded, plugin.ToString());
        }
    }

    private static string Describe(PluginHost host, string pluginId) =>
        host.Catalog.GetAll<IDescriber>().Single(e => e.Plugin.Id == pluginId).Value.Describe();

    /// <summary>The app's container, as each adapter builds it.</summary>
    protected interface IAppContainer : IAsyncDisposable
    {
        T Resolve<T>()
            where T : notnull;

        IEnumerable<T> ResolveAll<T>();
    }

    /// <summary>The host's <see cref="IEventLog"/>.</summary>
    protected sealed class EventLog : IEventLog
    {
        private readonly List<string> _entries = [];

        public IReadOnlyList<string> Entries
        {
            get
            {
                lock (_entries)
                {
                    return _entries.ToList();
                }
            }
        }

        public void Write(string entry)
        {
            lock (_entries)
            {
                _entries.Add(entry);
            }
        }
    }

    /// <summary>The host's own <see cref="ICellService"/>.</summary>
    protected sealed class HostCellService : ICellService
    {
        public string Name => "Host";
    }
}

using Alcove.Cells;
using Alcove.DependencyInjection;
using Alcove.DependencyInjection.Composition;
using Alcove.Hosting;
using Alcove.Install;
using Alcove.IntegrationTests.Infrastructure;
using Fixture.HostContracts;
using Microsoft.Extensions.DependencyInjection;

namespace Alcove.IntegrationTests.DependencyInjection;

/// <summary>The core's extension point, <see cref="ICellServicesFactory"/>, independently of either adapter.</summary>
[Collection(DependencyInjectionCollection.Name)]
public sealed class CellServicesFactoryTests : IAsyncLifetime
{
    private const string Alpha = "Fixture.DI.Alpha";
    private const string Beta = "Fixture.DI.Beta";
    private const string Plain = "Fixture.DI.Plain";

    private readonly DependencyInjectionFixtures _fixtures;
    private readonly TempDirectory _root = new("alcove-cell-services");
    private readonly List<PluginHost> _hosts = [];
    private readonly List<string> _log = [];
    private readonly ServiceProvider _services;

    public CellServicesFactoryTests(DependencyInjectionFixtures fixtures)
    {
        _fixtures = fixtures;
        _services = new ServiceCollection()
            .AddSingleton<IEventLog>(new EventLog(_log))
            .AddSingleton<ICellService>(new HostCellService())
            .BuildServiceProvider();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts)
        {
            await host.DisposeAsync();
        }

        await _services.DisposeAsync();
        _root.DisposeBestEffort();
    }

    [Fact]
    public async Task TheFactory_IsCalledOncePerCell_WithItsServicePlugins_BeforeAnyOfThemRegisters()
    {
        var factory = new RecordingFactory(_log);
        var host = await InstallAndLoadAsync([Alpha, Beta, Plain], factory, o => o.Cells.Add(new CellDefinition("Shared") { Plugins = { Alpha, Plain } }));

        Assert.All(host.Plugins, p => Assert.Equal(PluginStatus.Loaded, p.Status));
        Assert.Equal(["Shared: Fixture.DI.Alpha", "Fixture.DI.Beta: Fixture.DI.Beta"], factory.Calls);
        var created = _log.IndexOf("services for Shared");
        Assert.True(created >= 0 && created < _log.FindIndex(e => e.StartsWith("Alpha Register", StringComparison.Ordinal)), string.Join(Environment.NewLine, _log));

        // IPluginContext.Services and the activator both get the cell's provider.
        Assert.Contains("Alpha Register: formatter True, cell service Alpha (ms)", _log);
        Assert.Equal("Plain sees Alpha (ms), Host", host.Catalog.GetAll<IDescriber>().Single(e => e.Plugin.Id == Plain).Value.Describe());
    }

    [Fact]
    public async Task AFactoryThatThrows_FaultsTheCellsPlugins_AndTheOtherCellsLoad()
    {
        var factory = new RecordingFactory(_log) { ThrowFor = "Shared" };
        var host = await InstallAndLoadAsync([Alpha, Beta, Plain], factory, o => o.Cells.Add(new CellDefinition("Shared") { Plugins = { Alpha, Plain } }));

        foreach (var id in new[] { Alpha, Plain })
        {
            var plugin = host.Plugins.Single(p => p.Id == id);
            Assert.Equal(PluginStatus.Faulted, plugin.Status);
            Assert.Contains("creating its cell's services", plugin.Reason);
            Assert.Contains("no services for Shared", plugin.Fault!.ToString());
        }

        Assert.Equal(PluginStatus.Loaded, host.Plugins.Single(p => p.Id == Beta).Status);
    }

    private async Task<PluginHost> InstallAndLoadAsync(string[] plugins, ICellServicesFactory factory, Action<PluginHostOptions> configure)
    {
        var inventory = HostInventory.FromCurrentProcess([HostingFixtures.ContractsPackage, "Alcove.DependencyInjection"]);
        await new LocalPackageInstaller(inventory, _root.Path).InstallAsync(_fixtures.Packages, plugins, Ct);
        var options = new PluginHostOptions
        {
            Root = _root.Path,
            PluginPackageType = "FixturePlugin",
            HostOwnedPackages = { HostingFixtures.ContractsPackage },
            CellServicesFactory = factory,
        }.UseDependencyInjection(_services);
        configure(options);
        var host = PluginHost.Create(options);
        _hosts.Add(host);
        await host.LoadAsync(Ct);
        return host;
    }

    /// <summary>Records each call, then builds the provider with the Microsoft DI add-on's factory.</summary>
    private sealed class RecordingFactory(List<string> log) : ICellServicesFactory
    {
        public List<string> Calls { get; } = [];

        public string? ThrowFor { get; init; }

        public IReadOnlyCollection<string> ServicePluginInterfaces { get; } = [typeof(IServicePlugin).FullName!];

        public IServiceProvider CreateServices(CellServicesContext context)
        {
            Calls.Add($"{context.Cell}: {string.Join(", ", context.Plugins.Select(p => p.Plugin.Id))}");
            if (context.Cell == ThrowFor)
            {
                throw new InvalidOperationException($"no services for {context.Cell}");
            }

            log.Add($"services for {context.Cell}");
            return ServiceCollectionCellServicesFactory.Instance.CreateServices(context);
        }
    }

    private sealed class EventLog(List<string> log) : IEventLog
    {
        public void Write(string entry) => log.Add(entry);
    }

    private sealed class HostCellService : ICellService
    {
        public string Name => "Host";
    }
}

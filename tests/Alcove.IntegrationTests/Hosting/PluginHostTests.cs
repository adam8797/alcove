using System.Runtime.Loader;
using Alcove.Cells;
using Alcove.Hosting;
using Alcove.IntegrationTests.Infrastructure;
using Alcove.Storage;
using Alcove.Validation;
using Fixture.HostContracts;

namespace Alcove.IntegrationTests.Hosting;

/// <summary>
/// <see cref="PluginHost"/> end to end with real plugin packages (tests/fixtures/Hosting) installed into a
/// temporary plugin root without a feed: the implementation plan's Phase 3 "Done when" scenarios.
/// </summary>
public sealed class PluginHostTests(HostingFixtures fixtures) : IDisposable
{
    private const string UserA = "Fixture.Hosting.UserA";
    private const string UserB = "Fixture.Hosting.UserB";
    private const string Provider = "Fixture.Hosting.Provider";
    private const string Consumer = "Fixture.Hosting.Consumer";
    private const string Consumer3 = "Fixture.Hosting.Consumer3";
    private const string Middle = "Fixture.Hosting.Middle";
    private const string Top = "Fixture.Hosting.Top";
    private const string Faulty = "Fixture.Hosting.Faulty";
    private const string FaultyInit = "Fixture.Hosting.FaultyInit";
    private const string AfterFaulty = "Fixture.Hosting.AfterFaulty";
    private const string Isolated = "Fixture.Hosting.Isolated";
    private const string BadExtension = "Fixture.Hosting.BadExtension";

    private static readonly Lazy<HostInventory> s_inventory = new(() => HostInventory.FromCurrentProcess([HostingFixtures.ContractsPackage]));

    private readonly PluginRootBuilder _root = new();
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
    }

    [Fact]
    public async Task TwoPluginsInSeparateCells_UseConflictingVersionsOfOneDependency()
    {
        var host = await InstallAndLoadAsync([UserA, UserB]);

        AssertLoaded(host, UserA, UserB);
        Assert.Equal(UserA, Plugin(host, UserA).Cell);
        Assert.Equal(UserB, Plugin(host, UserB).Cell);
        Assert.Equal("UserA uses Dep 1.0 in 'Fixture.Hosting.UserA'", Describe(host, UserA));
        Assert.Equal("UserB uses Dep 2.0 in 'Fixture.Hosting.UserB' and NewIn2 present; services: False", Describe(host, UserB));
        Assert.Empty((await host.ValidateAsync(Ct)).Issues);
    }

    [Fact]
    public async Task APluginDependingOnAnotherAcrossCells_GetsTheProvidersExposedDependency()
    {
        var states = await _root.InstallAsync(s_inventory.Value, fixtures.Packages, Consumer);
        Assert.Equal([Provider, Consumer], states.Select(s => s.Id));
        Assert.Equal(["Fixture.Dep"], states[0].ExposedDependencies);
        var host = await LoadAsync();

        AssertLoaded(host, Provider, Consumer);
        Assert.Equal(
            "Consumer: IS my Widget: Widget(Dep 2.0 in 'Fixture.Hosting.Provider'); ProviderService in 'Fixture.Hosting.Provider'",
            Describe(host, Consumer));
        Assert.Equal(
            "Provider: ProviderService in 'Fixture.Hosting.Provider', Widget(Dep 2.0 in 'Fixture.Hosting.Provider')",
            Describe(host, Provider));

        // A plugin is loaded once in the process: the consumer's cell holds neither the provider nor Dep.
        var consumerCell = host.CellEnvironment.FindCell(Consumer)!;
        Assert.DoesNotContain(consumerCell.Assemblies, a => a.GetName().Name is Provider or "Fixture.Dep");
    }

    [Fact]
    public async Task AChainOfThreePlugins_LoadsDependenciesFirstAndMapsTheTransitiveClosure()
    {
        var states = await _root.InstallAsync(s_inventory.Value, fixtures.Packages, Top);
        Assert.Equal([Provider, Middle, Top], states.Select(s => s.Id));
        var loadOrder = new List<string>();
        var host = await LoadAsync(configure: null, h => h.Catalog.ExtensionsAdded += (_, e) => loadOrder.Add(e.Plugin.Id));

        AssertLoaded(host, Provider, Middle, Top);
        Assert.Equal([Provider, Middle, Top], loadOrder);
        Assert.Equal("Top: ProviderService in 'Fixture.Hosting.Provider'; same ProviderService type: True", Describe(host, Top));
        Assert.Equal("Middle: ProviderService in 'Fixture.Hosting.Provider'", Describe(host, Middle));
    }

    [Fact]
    public async Task AFaultingPlugin_DoesntStopTheRest_AndItsDependentsArentLoaded()
    {
        var faults = new List<(string Id, Type Exception, PluginStatus Status)>();
        var host = await InstallAndLoadAsync(
            [Faulty, FaultyInit, AfterFaulty, UserA],
            configure: null,
            h => h.PluginFaulted += (_, e) => faults.Add((e.Plugin.Id, e.Fault.GetType(), e.Plugin.Status)));

        AssertLoaded(host, UserA);
        var faulty = Plugin(host, Faulty);
        Assert.Equal(PluginStatus.Faulted, faulty.Status);
        Assert.IsType<InvalidOperationException>(faulty.Fault);
        Assert.Contains("Register", faulty.Reason);
        Assert.IsType<IOException>(Plugin(host, FaultyInit).Fault);
        Assert.Contains("InitializeAsync", Plugin(host, FaultyInit).Reason);

        var after = Plugin(host, AfterFaulty);
        Assert.Equal(PluginStatus.NotLoaded, after.Status);
        Assert.Contains(Faulty, after.Reason);
        Assert.Null(after.Fault);

        Assert.Equal(
            [(Faulty, typeof(InvalidOperationException), PluginStatus.Faulted), (FaultyInit, typeof(IOException), PluginStatus.Faulted)],
            faults);

        // A faulted plugin contributes nothing, even what it registered before throwing.
        Assert.Equal([UserA], host.Catalog.GetAll<IDescriber>().Select(e => e.Plugin.Id));
    }

    [Fact]
    public async Task AnExtensionThatCantBeCreated_FaultsItsPlugin()
    {
        var faults = new List<string>();
        var host = await InstallAndLoadAsync([BadExtension, UserB], configure: null, h => h.PluginFaulted += (_, e) => faults.Add(e.Plugin.Id));
        AssertLoaded(host, BadExtension, UserB);

        var describer = host.Catalog.GetAll<IDescriber>().Single(e => e.Plugin.Id == BadExtension);
        var ex = await Assert.ThrowsAsync<PluginException>(() => Task.FromResult(describer.Value));

        Assert.Equal(BadExtension, ex.PluginId);
        Assert.IsType<FormatException>(ex.InnerException);
        Assert.Equal(PluginStatus.Faulted, Plugin(host, BadExtension).Status);
        Assert.Equal([BadExtension], faults);

        // A type without a public parameterless constructor can't be created by the default activator.
        var greeter = host.Catalog.GetAll<IGreeter, GreeterAttribute>().Single(e => e.Metadata.Language == "none");
        Assert.IsType<MissingMethodException>(Assert.Throws<PluginException>(() => greeter.CreateInstance()).InnerException);
        Assert.Equal([BadExtension], faults);   // the first fault is kept; no second event

        Assert.Equal(PluginStatus.Loaded, Plugin(host, UserB).Status);
    }

    [Fact]
    public async Task FailFast_ThrowsOnTheFirstFault()
    {
        await _root.InstallAsync(s_inventory.Value, fixtures.Packages, Faulty, UserA);
        var host = Create(o => o.FailFast = true);

        var ex = await Assert.ThrowsAsync<PluginException>(() => host.LoadAsync(Ct));

        Assert.Equal(Faulty, ex.PluginId);
        Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Equal(PluginStatus.Faulted, Plugin(host, Faulty).Status);
        Assert.Equal(PluginStatus.NotLoaded, Plugin(host, UserA).Status);   // after Faulty in load order
        Assert.Contains("FailFast", Plugin(host, UserA).Reason);
    }

    [Fact]
    public async Task AnExposedDependencyConflict_IsReportedAndOnlyTheConsumerIsBlocked()
    {
        await _root.InstallAsync(s_inventory.Value, fixtures.Packages, Consumer3, UserA);
        var host = Create();

        var report = await host.ValidateAsync(Ct);

        var conflict = Assert.Single(report.Errors);
        Assert.Equal((ValidationCodes.ExposedDependencyConflict, Consumer3, Consumer3), (conflict.Code, conflict.PluginId, conflict.Cell));
        Assert.Contains("Fixture.Dep 3.0.0", conflict.Message);

        await host.LoadAsync(Ct);
        AssertLoaded(host, Provider, UserA);
        Assert.Equal(PluginStatus.NotLoaded, Plugin(host, Consumer3).Status);
        Assert.Contains("Fixture.Dep", Plugin(host, Consumer3).Reason);
    }

    [Fact]
    public async Task AnIsolatedPluginPutWithOthersByTheApp_BlocksThatCell()
    {
        await _root.InstallAsync(s_inventory.Value, fixtures.Packages, Isolated, UserA, UserB);
        var host = Create(o => o.Cells.Add(new CellDefinition("Together") { Plugins = { Isolated, UserA } }));

        var report = await host.ValidateAsync(Ct);

        Assert.Equal(
            [(Isolated, "Together"), (UserA, "Together")],
            report.Errors.Select(e => (e.PluginId!, e.Cell!)).Order());
        Assert.All(report.Errors, e => Assert.Equal(ValidationCodes.IsolatedPluginNotAlone, e.Code));

        await host.LoadAsync(Ct);
        Assert.Equal(PluginStatus.NotLoaded, Plugin(host, Isolated).Status);
        Assert.Equal(PluginStatus.NotLoaded, Plugin(host, UserA).Status);
        AssertLoaded(host, UserB);
    }

    [Fact]
    public async Task AnIsolatedPluginAlone_Loads()
    {
        var host = await InstallAndLoadAsync([Isolated, UserA]);

        AssertLoaded(host, Isolated, UserA);
        Assert.Equal("Isolated", Describe(host, Isolated));
    }

    [Fact]
    public async Task PluginsShareAnAppDefinedCell()
    {
        // UserB and Provider both use Dep 2.0: one copy in the shared cell.
        var host = await InstallAndLoadAsync([UserB, Provider], o => o.Cells.Add(new CellDefinition("Shared") { Plugins = { UserB, Provider } }));

        AssertLoaded(host, UserB, Provider);
        Assert.All(host.Plugins, p => Assert.Equal("Shared", p.Cell));
        Assert.Contains("Dep 2.0 in 'Shared'", Describe(host, UserB));
        Assert.Contains("Widget(Dep 2.0 in 'Shared')", Describe(host, Provider));
    }

    [Fact]
    public async Task DisposeAsync_DisposesSingletonsAndPluginsInReverseLoadOrder()
    {
        var log = new EventLog();
        await _root.InstallAsync(s_inventory.Value, fixtures.Packages, "Fixture.Hosting.Disposable.Top");
        var host = Create(o => o.Services = new Services(log));
        await host.LoadAsync(Ct);
        AssertLoaded(host, "Fixture.Hosting.Disposable.Base", "Fixture.Hosting.Disposable.Top");

        // Created in an order unrelated to the load order: Base's second, Top's first, Base's first, Top's second.
        _ = host.Catalog.GetAll<IGreeter>().Single(e => e.Plugin.Id.EndsWith("Base", StringComparison.Ordinal)).Value;
        foreach (var extension in host.Catalog.GetAll<IDescriber>().Reverse())
        {
            _ = extension.Value;
        }

        _ = host.Catalog.GetAll<IGreeter>().Single(e => e.Plugin.Id.EndsWith("Top", StringComparison.Ordinal)).Value;

        await host.DisposeAsync();

        Assert.Equal(
            [
                "Top second singleton disposed", "Top first singleton disposed", "Top plugin disposed",
                "Base first singleton disposed", "Base second singleton disposed", "Base plugin disposed",
            ],
            log.Entries);
        Assert.Throws<ObjectDisposedException>(() => host.Catalog.GetAll<IDescriber>()[0].Value);
        await host.DisposeAsync();   // idempotent
        Assert.Equal(6, log.Entries.Count);
    }

    [Fact]
    public async Task TwoHostsInOneProcess_HaveTheirOwnCells()
    {
        await _root.InstallAsync(s_inventory.Value, fixtures.Packages, UserA, Consumer);
        var first = Create();
        var second = Create();
        await first.LoadAsync(Ct);
        await second.LoadAsync(Ct);

        AssertLoaded(first, UserA, Provider, Consumer);
        AssertLoaded(second, UserA, Provider, Consumer);
        var one = first.Catalog.GetAll<IDescriber>().Single(e => e.Plugin.Id == Consumer).Value;
        var two = second.Catalog.GetAll<IDescriber>().Single(e => e.Plugin.Id == Consumer).Value;
        Assert.NotSame(one.GetType(), two.GetType());
        Assert.NotSame(AssemblyLoadContext.GetLoadContext(one.GetType().Assembly), AssemblyLoadContext.GetLoadContext(two.GetType().Assembly));
        Assert.Equal(one.Describe(), two.Describe());

        await first.DisposeAsync();
        Assert.Equal(two.Describe(), second.Catalog.GetAll<IDescriber>().Single(e => e.Plugin.Id == Consumer).Value.Describe());
    }

    [Fact]
    public async Task ThePluginContext_GivesDataDirectoryLoggerAndHostServices()
    {
        var log = new EventLog();
        var host = await InstallAndLoadAsync([UserB], o => o.Services = new Services(log));

        Assert.Equal("1.0.0", File.ReadAllText(Path.Combine(_root.Root, "data", UserB, "initialized.txt")));
        Assert.Contains(_logs.Entries, e => e.Category == UserB && e.Message == "UserB initialized");
        Assert.EndsWith("services: True", Describe(host, UserB));
    }

    [Fact]
    public async Task TheCatalog_ListsMetadataWithoutCreatingAndEnforcesLifetimes()
    {
        var host = await InstallAndLoadAsync([UserA, UserB]);

        var all = host.Catalog.GetAll<IGreeter>();
        Assert.Equal(4, all.Count);   // UserA's attribute + UserB's three registrations
        var typed = host.Catalog.GetAll<IGreeter, GreeterAttribute>();
        Assert.Equal(["de", "en", "fr"], typed.Select(e => e.Metadata.Language).Order());   // PlainGreeter has no metadata

        var german = typed.Single(e => e.Metadata.Language == "de");
        Assert.Equal((UserA, ExtensionLifetime.Transient), (german.Plugin.Id, german.Lifetime));
        Assert.NotSame(german.CreateInstance(), german.CreateInstance());
        Assert.Equal("Hallo, Ada", german.CreateInstance().Greet("Ada"));
        Assert.Contains("transient", Assert.Throws<InvalidOperationException>(() => german.Value).Message);

        var english = typed.Single(e => e.Metadata.Language == "en");
        Assert.Same(english.Value, english.Value);
        Assert.Same(english.Value, all.Single(e => ReferenceEquals(e.Metadata, english.Metadata)).Value);
        Assert.Contains("singleton", Assert.Throws<InvalidOperationException>(() => english.CreateInstance()).Message);

        // Implementing an interface isn't enough to be discovered.
        Assert.Single(host.Catalog.GetAll<IDescriber>(), e => e.Plugin.Id == UserA);
    }

    [Fact]
    public async Task Observe_BeforeLoading_SeesEveryExtensionOnTheLoadingThread()
    {
        await _root.InstallAsync(s_inventory.Value, fixtures.Packages, UserA, Top);
        var host = Create();
        var seen = new List<(string Plugin, int Thread)>();
        using var subscription = host.Catalog.Observe<IDescriber>(e => seen.Add((e.Plugin.Id, Environment.CurrentManagedThreadId)));
        var events = 0;
        host.Catalog.ExtensionsAdded += (_, e) => events++;

        var thread = Environment.CurrentManagedThreadId;
        await host.LoadAsync(Ct);

        Assert.Equal([Provider, Middle, Top, UserA], seen.Select(s => s.Plugin));
        Assert.Equal(4, events);
        Assert.All(seen, s => Assert.Equal(thread, s.Thread));   // none of these plugins' code awaits anything

        var late = new List<string>();
        using var lateSubscription = host.Catalog.Observe<IDescriber>(e => late.Add(e.Plugin.Id));
        Assert.Equal([Provider, Middle, Top, UserA], late);
    }

    [Fact]
    public async Task AnEmptyRoot_LoadsNothing()
    {
        var host = Create();

        await host.LoadAsync(Ct);

        Assert.Empty(host.Plugins);
        Assert.Empty(host.Catalog.GetAll<IDescriber>());
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.LoadAsync(Ct));
    }

    [Fact]
    public async Task AnUnreadableStateFile_FailsLoadAsync()
    {
        File.WriteAllText(Path.Combine(_root.Root, "state.json"), "{ not json");
        var host = Create();

        var report = await host.ValidateAsync(Ct);
        var ex = await Assert.ThrowsAsync<PluginException>(() => host.LoadAsync(Ct));

        Assert.Equal(ValidationCodes.StateFileUnreadable, Assert.Single(report.Errors).Code);
        Assert.True(Assert.Single(report.Errors).IsGlobal);
        Assert.True(ex.Report!.HasGlobalErrors);
        Assert.Null(ex.PluginId);

        // Nothing was loaded, so LoadAsync may be called again once the root is fixed.
        File.Delete(Path.Combine(_root.Root, "state.json"));
        await host.LoadAsync(Ct);
    }

    [Fact]
    public async Task AChangedFile_BlocksOnlyThatPlugin()
    {
        await _root.InstallAsync(s_inventory.Value, fixtures.Packages, UserA, UserB);
        var dll = Path.Combine(_root.Layout.GetPackageDirectory(UserA, NuGet.Versioning.NuGetVersion.Parse("1.0.0")), "lib", "net10.0", UserA + ".dll");
        File.AppendAllText(dll, "tampered");
        var host = Create();

        var error = Assert.Single((await host.ValidateAsync(Ct)).Errors);
        Assert.Equal((ValidationCodes.FileChanged, UserA), (error.Code, error.PluginId));

        await host.LoadAsync(Ct);
        Assert.Equal(PluginStatus.NotLoaded, Plugin(host, UserA).Status);
        AssertLoaded(host, UserB);
    }

    [Fact]
    public async Task ADisabledPlugin_IsSkipped_AndItsDependentsArentLoaded()
    {
        await _root.InstallAsync(s_inventory.Value, fixtures.Packages, Consumer, UserA);
        var state = new StateStore(_root.Root).Read();
        await _root.WriteStateAsync(state with
        {
            Plugins = state.Plugins.Select(p => p.Id == Provider ? p with { Enabled = false } : p).ToList(),
        });
        var host = Create();

        await host.LoadAsync(Ct);

        Assert.Equal(PluginStatus.Disabled, Plugin(host, Provider).Status);
        Assert.Equal(PluginStatus.NotLoaded, Plugin(host, Consumer).Status);
        Assert.Contains("disabled", Plugin(host, Consumer).Reason);
        AssertLoaded(host, UserA);
    }

    [Fact]
    public async Task APluginDependencyOutsideItsRange_BlocksTheDependent_ButTheDependencyLoads()
    {
        // As if Provider had been updated on its own to 2.0.0 while Consumer declares Provider [1.0.0, ).
        await _root.InstallAsync(s_inventory.Value, fixtures.Packages, Consumer, UserA);
        var state = new StateStore(_root.Root).Read();
        await _root.WriteStateAsync(state with
        {
            Plugins = state.Plugins.Select(p => p.Id != Consumer ? p : p with
            {
                Dependencies = p.Dependencies.Select(d => d.Id != Provider ? d : d with
                {
                    RequestedBy = [new DependencyRequest(Consumer, NuGet.Versioning.VersionRange.Parse("[2.0.0, 3.0.0)"))],
                }).ToList(),
            }).ToList(),
        });
        var host = Create();

        var error = Assert.Single((await host.ValidateAsync(Ct)).Errors);
        Assert.Equal((ValidationCodes.PluginDependencyConflict, Consumer), (error.Code, error.PluginId));
        Assert.Contains("[2.0.0, 3.0.0)", error.Message);
        Assert.Contains("version 1.0.0 is installed", error.Message);

        await host.LoadAsync(Ct);
        AssertLoaded(host, Provider, UserA);
        Assert.Equal(PluginStatus.NotLoaded, Plugin(host, Consumer).Status);
    }

    private async Task<PluginHost> InstallAndLoadAsync(string[] plugins, Action<PluginHostOptions>? configure = null, Action<PluginHost>? beforeLoad = null)
    {
        await _root.InstallAsync(s_inventory.Value, fixtures.Packages, plugins);
        return await LoadAsync(configure, beforeLoad);
    }

    private async Task<PluginHost> LoadAsync(Action<PluginHostOptions>? configure = null, Action<PluginHost>? beforeLoad = null)
    {
        var host = Create(configure);
        beforeLoad?.Invoke(host);
        await host.LoadAsync(Ct);
        return host;
    }

    private PluginHost Create(Action<PluginHostOptions>? configure = null)
    {
        var options = new PluginHostOptions
        {
            Root = _root.Root,
            HostOwnedPackages = { HostingFixtures.ContractsPackage },
            LoggerFactory = _logs,
        };
        configure?.Invoke(options);
        var host = PluginHost.Create(options);
        _hosts.Add(host);
        return host;
    }

    private static PluginInfo Plugin(PluginHost host, string id) => host.Plugins.Single(p => p.Id == id);

    private static void AssertLoaded(PluginHost host, params string[] ids)
    {
        foreach (var id in ids)
        {
            var plugin = Plugin(host, id);
            Assert.True(plugin.Status == PluginStatus.Loaded, $"{plugin} (fault: {plugin.Fault})");
        }
    }

    private static string Describe(PluginHost host, string pluginId) =>
        host.Catalog.GetAll<IDescriber>().Single(e => e.Plugin.Id == pluginId).Value.Describe();

    private sealed class EventLog : IEventLog
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

    private sealed class Services(IEventLog log) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(IEventLog) ? log : null;
    }
}

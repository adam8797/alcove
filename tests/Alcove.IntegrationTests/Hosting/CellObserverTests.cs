using System.Reflection;
using System.Runtime.Loader;
using Alcove.Catalog;
using Alcove.Cells;
using Alcove.Feeds;
using Alcove.Install;
using Alcove.IntegrationTests.Infrastructure;
using Fixture.HostContracts;
using Microsoft.Extensions.Logging;

namespace Alcove.IntegrationTests.Hosting;

/// <summary>
/// <see cref="ICellObserver"/> (<see cref="PluginHostOptions.CellObservers"/>), the core extension point the WPF add-on
/// uses, with real plugin packages (tests/fixtures/Hosting) from a local folder feed.
/// </summary>
public sealed class CellObserverTests(HostingFixtures fixtures) : IDisposable
{
    private const string UserA = "Fixture.Hosting.UserA";
    private const string UserB = "Fixture.Hosting.UserB";
    private const string Provider = "Fixture.Hosting.Provider";
    private const string Dep = "Fixture.Dep";

    private readonly TempDirectory _root = new("alcove-observer");
    private readonly ListLoggerFactory _logs = new();
    private readonly List<PluginHost> _hosts = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (var host in _hosts)
        {
            host.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        _root.DisposeBestEffort();   // cells keep the root's assemblies loaded
    }

    [Fact]
    public async Task Observers_SeeEachCellBeforeAnythingLoads_AndEachPluginsAssembliesBeforeItRegisters()
    {
        var recorder = new Recorder();
        var installer = Create(o => o.CellObservers.Add(_ => recorder));
        await installer.LoadAsync(Ct);
        Assert.Equal(InstallStatus.Loaded, (await installer.InstallAsync(UserA, cancellationToken: Ct)).Status);

        // The same plugin at startup: the cell is created by LoadAsync.
        var startup = new Recorder();
        ILoggerFactory? given = null;
        var host = Create(o => o.CellObservers.Add(f => { given = f; return startup; }));
        host.Catalog.ExtensionsAdded += (_, e) => startup.Events.Add($"added {e.Plugin.Id}");
        await host.LoadAsync(Ct);

        Assert.Same(_logs, given);
        foreach (var events in new[] { recorder.Events, startup.Events.Where(e => !e.StartsWith("added", StringComparison.Ordinal)).ToList() })
        {
            Assert.Equal([$"created {UserA}: 0 loaded", $"loading {UserA} in {UserA}: contextual {UserA}"], events);
        }

        Assert.Equal($"added {UserA}", startup.Events[^1]);
        var cell = Assert.Single(startup.Created);
        Assert.Equal(UserA, cell.Name);
        Assert.Equal([UserA], cell.Plugins);
        Assert.Equal([Dep, UserA], cell.PrivateAssemblies.Select(a => a.Name));
        Assert.All(cell.PrivateAssemblies, a => Assert.True(File.Exists(a.Path), a.Path));
        Assert.Equal(("Fixture.Dep", "1.0.0", false), (cell.PrivateAssemblies[0].PackageId, cell.PrivateAssemblies[0].PackageVersion.ToNormalizedString(), cell.PrivateAssemblies[0].IsWpfRelevant));

        var (plugin, assemblies) = Assert.Single(startup.Loading);
        Assert.Same(host.Plugins.Single(p => p.Id == UserA), plugin);
        Assert.Equal([UserA], assemblies.Select(a => a.GetName().Name));
        Assert.Same(cell.LoadContext, AssemblyLoadContext.GetLoadContext(assemblies[0]));
    }

    [Fact]
    public async Task AHotInstallIntoAnExistingCell_ExtendsIt_AndOneIntoANewCellCreatesIt()
    {
        // Provider (Dep 2.0, exposed) and UserB (Dep >= 2.0) share a cell: UserB hot-loads into it with the same Dep.
        var recorder = new Recorder();
        var host = Create(o =>
        {
            o.Cells.Add(new CellDefinition("Shared") { Plugins = { Provider, UserB } });
            o.CellObservers.Add(_ => recorder);
        });
        await host.LoadAsync(Ct);

        Assert.Equal(InstallStatus.Loaded, (await host.InstallAsync(Provider, cancellationToken: Ct)).Status);
        Assert.Equal(InstallStatus.Loaded, (await host.InstallAsync(UserB, cancellationToken: Ct)).Status);
        Assert.Equal(InstallStatus.Loaded, (await host.InstallAsync(UserA, cancellationToken: Ct)).Status);

        Assert.Equal(
            [
                "created Shared: 0 loaded",
                "loading Fixture.Hosting.Provider in Shared: contextual Shared",
                "extended Shared: +Fixture.Hosting.UserB",
                "loading Fixture.Hosting.UserB in Shared: contextual Shared",
                $"created {UserA}: 0 loaded",
                $"loading {UserA} in {UserA}: contextual {UserA}",
            ],
            recorder.Events);
        var shared = recorder.Created[0];
        Assert.Equal([Provider, UserB], shared.Plugins);
        Assert.Equal([Dep, Provider, UserB], shared.PrivateAssemblies.Select(a => a.Name));
    }

    [Fact]
    public async Task AnObserverThatThrows_IsLoggedForCells_AndFaultsThePluginWhenItsAssembliesLoad()
    {
        var host = Create(o => o.CellObservers.Add(_ => new Throwing()));
        await host.LoadAsync(Ct);

        var result = await host.InstallAsync(UserA, cancellationToken: Ct);

        Assert.Equal(InstallStatus.Faulted, result.Status);
        var plugin = host.Plugins.Single(p => p.Id == UserA);
        Assert.Contains("running the cell observers", plugin.Reason);
        Assert.Equal("PluginLoading", plugin.Fault?.Message);
        var logged = Assert.Single(_logs.WithEventId(2009));
        Assert.Contains("CellCreated", logged.Message);
    }

    [Fact]
    public async Task EachHostGetsItsOwnObservers_AndDisposesThemAfterItsPlugins()
    {
        var created = new List<Recorder>();
        void Configure(PluginHostOptions o) => o.CellObservers.Add(_ =>
        {
            var recorder = new Recorder();
            created.Add(recorder);
            return recorder;
        });
        var first = Create(Configure);
        var second = Create(Configure);
        await first.LoadAsync(Ct);
        await first.InstallAsync(UserA, cancellationToken: Ct);

        await first.DisposeAsync();

        Assert.Equal(2, created.Count);
        Assert.Equal("disposed", created[0].Events[^1]);
        Assert.DoesNotContain("disposed", created[1].Events);
        await second.DisposeAsync();
        Assert.Equal(["disposed"], created[1].Events);
    }

    [Fact]
    public async Task ExtensionsAreCreatedInsideTheirCellsContextualReflectionScope_WhateverTheActivator()
    {
        // Why Alcove.Wpf needs no activator of its own: the core wraps every activator (Alcove.DependencyInjection's and
        // Alcove.Autofac's included) in the cell's scope, so WPF's fallback Assembly.Load calls during creation reach the cell.
        var activator = new RecordingActivator();
        var host = Create(o => o.Activator = activator);
        await host.LoadAsync(Ct);
        await host.InstallAsync(UserA, cancellationToken: Ct);

        _ = host.Catalog.GetAll<IDescriber>().Single(e => e.Plugin.Id == UserA).Value;

        Assert.Equal(UserA, Assert.Single(activator.Contexts));
    }

    private PluginHost Create(Action<PluginHostOptions> configure)
    {
        var options = new PluginHostOptions
        {
            Root = _root.Path,
            PluginPackageType = "FixturePlugin",
            HostOwnedPackages = { HostingFixtures.ContractsPackage },
            Feeds = { new PackageFeed(fixtures.PackagesDirectory) },
            LoggerFactory = _logs,
        };
        configure(options);
        var host = PluginHost.Create(options);
        _hosts.Add(host);
        return host;
    }

    private sealed class Recorder : ICellObserver, IDisposable
    {
        public List<string> Events { get; } = [];

        public List<CellInfo> Created { get; } = [];

        public List<(PluginInfo Plugin, IReadOnlyList<Assembly> Assemblies)> Loading { get; } = [];

        public void CellCreated(CellInfo cell)
        {
            Created.Add(cell);
            Events.Add($"created {cell.Name}: {cell.LoadContext.Assemblies.Count()} loaded");
        }

        public void CellExtended(CellInfo cell, IReadOnlyList<CellAssembly> added) =>
            Events.Add($"extended {cell.Name}: {string.Join(", ", added.Select(a => "+" + a.Name))}");

        public void PluginLoading(CellInfo cell, PluginInfo plugin, IReadOnlyList<Assembly> assemblies)
        {
            Loading.Add((plugin, assemblies));
            Events.Add($"loading {plugin.Id} in {cell.Name}: contextual {AssemblyLoadContext.CurrentContextualReflectionContext?.Name}");
        }

        public void Dispose() => Events.Add("disposed");
    }

    private sealed class Throwing : ICellObserver
    {
        public void CellCreated(CellInfo cell) => throw new InvalidOperationException("CellCreated");

        public void PluginLoading(CellInfo cell, PluginInfo plugin, IReadOnlyList<Assembly> assemblies) =>
            throw new InvalidOperationException("PluginLoading");
    }

    private sealed class RecordingActivator : IExtensionActivator
    {
        public List<string?> Contexts { get; } = [];

        public object CreateInstance(ExtensionActivationContext context)
        {
            Contexts.Add(AssemblyLoadContext.CurrentContextualReflectionContext?.Name);
            return context.Factory?.Invoke() ?? Activator.CreateInstance(context.ImplementationType!)!;
        }
    }
}

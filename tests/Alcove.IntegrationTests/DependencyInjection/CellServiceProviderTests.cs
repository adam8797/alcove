using Alcove.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using NuGet.Versioning;

namespace Alcove.IntegrationTests.DependencyInjection;

/// <summary>
/// The Microsoft DI add-on's cell view (<see cref="CellServiceProvider"/>): fallback semantics that Autofac's child
/// lifetime scopes have natively.
/// </summary>
public sealed class CellServiceProviderTests
{
    [Fact]
    public async Task ASingleService_ComesFromTheCellWhenItRegisteredOne_OtherwiseFromTheHost()
    {
        await using var host = Host(s => s.AddSingleton<IValue>(new Value("host")).AddSingleton(new HostOnly()));
        await using var cell = (CellServiceProvider)Cell(host, s => s.AddSingleton<IValue>(new Value("cell")));

        Assert.Equal("cell", cell.GetRequiredService<IValue>().Name);
        Assert.Same(host.GetRequiredService<HostOnly>(), cell.GetRequiredService<HostOnly>());
        Assert.Null(cell.GetService<Unregistered>());
    }

    [Fact]
    public async Task IEnumerable_IsTheHostsServicesThenTheCells()
    {
        await using var host = Host(s => s.AddSingleton<IValue>(new Value("host 1")).AddSingleton<IValue>(new Value("host 2")));
        await using var cell = (CellServiceProvider)Cell(host, s => s.AddSingleton<IValue>(new Value("cell")));

        Assert.Equal(["host 1", "host 2", "cell"], cell.GetServices<IValue>().Select(v => v.Name));
        Assert.IsType<IValue[]>(cell.GetService(typeof(IEnumerable<IValue>)));
        Assert.Empty(cell.GetServices<Unregistered>());
    }

    [Fact]
    public async Task CellServicesByTypeAndFactory_GetTheirDependenciesFromTheCellAndTheHost()
    {
        await using var host = Host(s => s.AddSingleton(new HostOnly()));
        await using var cell = (CellServiceProvider)Cell(host, s => s
            .AddSingleton<IValue>(new Value("cell"))
            .AddSingleton<ByType>()
            .AddTransient(p => new ByFactory(p.GetRequiredService<HostOnly>(), p.GetRequiredService<IValue>())));

        var byType = cell.GetRequiredService<ByType>();
        var byFactory = cell.GetRequiredService<ByFactory>();

        Assert.Same(host.GetRequiredService<HostOnly>(), byType.Host);
        Assert.Equal("cell", byType.Value.Name);
        Assert.Same(byType.Host, byFactory.Host);
        Assert.Same(cell, cell.GetRequiredService<IServiceProvider>());
        Assert.True(cell.GetRequiredService<IServiceProviderIsService>().IsService(typeof(HostOnly)));
    }

    [Fact]
    public async Task AScope_PairsACellScopeWithAHostScope()
    {
        await using var host = Host(s => s.AddScoped<HostScoped>());
        await using var cell = (CellServiceProvider)Cell(host, s => s.AddScoped<CellScoped>());

        CellScoped first;
        await using (var scope = ((IServiceProvider)cell).CreateAsyncScope())
        {
            first = scope.ServiceProvider.GetRequiredService<CellScoped>();
            Assert.Same(first, scope.ServiceProvider.GetRequiredService<CellScoped>());
            Assert.Same(scope.ServiceProvider.GetRequiredService<HostScoped>(), first.Host);
        }

        Assert.True(first.Disposed);
        Assert.True(first.Host.Disposed);
        await using var other = ((IServiceProvider)cell).CreateAsyncScope();
        Assert.NotSame(first, other.ServiceProvider.GetRequiredService<CellScoped>());
    }

    [Fact]
    public async Task KeyedServices_ComeFromTheCellWhenItRegisteredTheKey_OtherwiseFromTheHost()
    {
        await using var host = Host(s => s.AddKeyedSingleton<IValue>("a", new Value("host a")).AddKeyedSingleton<IValue>("b", new Value("host b")));
        await using var cell = (CellServiceProvider)Cell(host, s => s.AddKeyedSingleton<IValue>("a", new Value("cell a")));

        Assert.Equal("cell a", cell.GetRequiredKeyedService<IValue>("a").Name);
        Assert.Equal("host b", cell.GetRequiredKeyedService<IValue>("b").Name);
        Assert.Null(cell.GetKeyedService<IValue>("c"));
    }

    [Fact]
    public async Task DisposingTheCell_DisposesItsServices_AndLeavesTheHostsAlone()
    {
        await using var host = Host(s => s.AddSingleton<HostScoped>());
        var hostService = host.GetRequiredService<HostScoped>();
        var cell = (CellServiceProvider)Cell(host, s => s.AddSingleton<CellScoped>());
        var cellService = cell.GetRequiredService<CellScoped>();

        await cell.DisposeAsync();

        Assert.True(cellService.Disposed);
        Assert.False(hostService.Disposed);
    }

    private static ServiceProvider Host(Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection();
        configure(services);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static IServiceProvider Cell(IServiceProvider host, Action<IServiceCollection> configure)
    {
        var plugin = new PluginInfo("Test.Plugin", NuGetVersion.Parse("1.0.0"), "cell", PluginStatus.NotLoaded, null);
        var context = new CellServicesContext("cell", host, [new CellServicesPlugin(plugin, new ServicePlugin(configure))]);
        return ServiceCollectionCellServicesFactory.Instance.CreateServices(context);
    }

    public interface IValue
    {
        string Name { get; }
    }

    private sealed record Value(string Name) : IValue;

    public sealed class HostOnly;

    public sealed class Unregistered;

    public sealed class ByType(HostOnly host, IValue value)
    {
        public HostOnly Host => host;

        public IValue Value => value;
    }

    public sealed class ByFactory(HostOnly host, IValue value)
    {
        public HostOnly Host => host;

        public IValue Value => value;
    }

    public sealed class HostScoped : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    public sealed class CellScoped(HostScoped host) : IDisposable
    {
        public HostScoped Host => host;

        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    private sealed class ServicePlugin(Action<IServiceCollection> configure) : IPlugin, IServicePlugin
    {
        public void ConfigureServices(IServiceCollection services) => configure(services);

        public void Register(IExtensionRegistrar registrar, IPluginContext context)
        {
        }
    }
}

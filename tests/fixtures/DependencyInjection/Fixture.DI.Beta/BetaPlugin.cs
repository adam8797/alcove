using Alcove;
using Autofac;
using Fixture.HostContracts;
using Microsoft.Extensions.DependencyInjection;

namespace Fixture.DI.Beta;

/// <summary>Registers a cell service by factory that needs a host service, and an extension by type.</summary>
public sealed class BetaPlugin : IPlugin, Alcove.DependencyInjection.IServicePlugin, Alcove.Autofac.IServicePlugin, IDisposable
{
    private IEventLog? _log;

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<ICellService>(new NamedCellService("Beta (ms)"));
        services.AddSingleton(provider => new BetaTracker(provider.GetRequiredService<IEventLog>()));
    }

    public void ConfigureContainer(ContainerBuilder builder)
    {
        builder.RegisterInstance(new NamedCellService("Beta (autofac)")).As<ICellService>();
        builder.Register(context => new BetaTracker(context.Resolve<IEventLog>())).SingleInstance();
    }

    public void Register(IExtensionRegistrar registrar, IPluginContext context)
    {
        _log = (IEventLog?)context.Services.GetService(typeof(IEventLog));
        registrar.Add<IDescriber, BetaDescriber>();
    }

    public void Dispose() => _log?.Write("Beta plugin disposed");
}

public sealed class NamedCellService(string name) : ICellService
{
    public string Name => name;
}

public sealed class BetaTracker(IEventLog log) : IDisposable
{
    public void Dispose() => log.Write("Beta cell service disposed");
}

public sealed class BetaDescriber(BetaTracker tracker, IEnumerable<ICellService> services, IEventLog log) : IDescriber, IDisposable
{
    public BetaTracker Tracker => tracker;

    public string Describe() => "Beta sees " + string.Join(", ", services.Select(s => s.Name).Order(StringComparer.Ordinal));

    public void Dispose() => log.Write("Beta extension disposed");
}

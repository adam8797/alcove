using Alcove;
using Autofac;
using Fixture.HostContracts;
using Microsoft.Extensions.DependencyInjection;

namespace Fixture.DI.Alpha;

/// <summary>Registers its services with whichever add-on the host uses, then extensions by type (constructor injection).</summary>
public sealed class AlphaPlugin : IPlugin, Alcove.DependencyInjection.IServicePlugin, Alcove.Autofac.IServicePlugin, IDisposable
{
    private IEventLog? _log;

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<ICellService>(new NamedCellService("Alpha (ms)"));
        services.AddSingleton<AlphaFormatter>();
    }

    public void ConfigureContainer(ContainerBuilder builder)
    {
        builder.RegisterInstance(new NamedCellService("Alpha (autofac)")).As<ICellService>();
        builder.RegisterType<AlphaFormatter>().SingleInstance();
    }

    public void Register(IExtensionRegistrar registrar, IPluginContext context)
    {
        _log = (IEventLog?)context.Services.GetService(typeof(IEventLog));
        var formatter = context.Services.GetService(typeof(AlphaFormatter)) is AlphaFormatter;
        var cellService = (ICellService?)context.Services.GetService(typeof(ICellService));
        _log?.Write($"Alpha Register: formatter {formatter}, cell service {cellService?.Name}");

        registrar.Add<IDescriber, AlphaDescriber>();
        registrar.Add<IGreeter, AlphaGreeter>(lifetime: ExtensionLifetime.Transient);
    }

    public void Dispose() => _log?.Write("Alpha plugin disposed");
}

public sealed class NamedCellService(string name) : ICellService
{
    public string Name => name;
}

/// <summary>A cell service registered by type that needs a host service.</summary>
public sealed class AlphaFormatter(IEventLog log) : IDisposable
{
    public Guid Id { get; } = Guid.NewGuid();

    public string Format(IEnumerable<ICellService> services) => string.Join(", ", services.Select(s => s.Name).Order(StringComparer.Ordinal));

    public void Dispose() => log.Write("Alpha cell service disposed");
}

/// <summary>A singleton extension: a cell service, every visible <see cref="ICellService"/> and a host service.</summary>
public sealed class AlphaDescriber(AlphaFormatter formatter, IEnumerable<ICellService> services, IEventLog log) : IDescriber, IDisposable
{
    public string Describe() => "Alpha sees " + formatter.Format(services);

    public void Dispose() => log.Write("Alpha extension disposed");
}

/// <summary>A transient extension: a new instance each time, with the cell's one formatter.</summary>
public sealed class AlphaGreeter(AlphaFormatter formatter) : IGreeter
{
    private readonly Guid _id = Guid.NewGuid();

    public string Greet(string name) => $"{_id}/{formatter.Id}";
}

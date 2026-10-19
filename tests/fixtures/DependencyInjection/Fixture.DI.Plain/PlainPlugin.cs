using Alcove;
using Fixture.HostContracts;

namespace Fixture.DI.Plain;

/// <summary>Registers no services; its extension is still created with constructor injection.</summary>
public sealed class PlainPlugin : IPlugin
{
    public void Register(IExtensionRegistrar registrar, IPluginContext context) => registrar.Add<IDescriber, PlainDescriber>();
}

/// <summary>Needs a host service, and lists the <see cref="ICellService"/>s its cell sees.</summary>
public sealed class PlainDescriber(IEventLog log, IEnumerable<ICellService> services) : IDescriber
{
    public string Describe()
    {
        log.Write("Plain described");
        return "Plain sees " + string.Join(", ", services.Select(s => s.Name).Order(StringComparer.Ordinal));
    }
}

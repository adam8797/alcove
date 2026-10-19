using Alcove;
using Fixture.HostContracts;

namespace Fixture.Hosting.BadExtension;

public sealed class BadExtensionPlugin : IPlugin
{
    public void Register(IExtensionRegistrar registrar, IPluginContext context)
    {
        registrar.Add<IDescriber>(() => throw new FormatException("BadExtension can't be created."));
        registrar.Add<IGreeter, NoDefaultConstructorGreeter>(new GreeterAttribute { Language = "none" }, ExtensionLifetime.Transient);
    }
}

public sealed class NoDefaultConstructorGreeter(string greeting) : IGreeter
{
    public string Greet(string name) => greeting + name;
}

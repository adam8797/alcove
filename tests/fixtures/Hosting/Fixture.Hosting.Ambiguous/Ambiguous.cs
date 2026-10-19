using Alcove;
using Fixture.HostContracts;

namespace Fixture.Hosting.Ambiguous;

public sealed class FirstPlugin : IPlugin
{
    public void Register(IExtensionRegistrar registrar, IPluginContext context) =>
        registrar.Add<IDescriber>(() => new AmbiguousDescriber("first"));
}

public sealed class SecondPlugin : IPlugin
{
    public void Register(IExtensionRegistrar registrar, IPluginContext context) =>
        registrar.Add<IDescriber>(() => new AmbiguousDescriber("second"));
}

public sealed class AmbiguousDescriber(string text) : IDescriber
{
    public string Describe() => text;
}

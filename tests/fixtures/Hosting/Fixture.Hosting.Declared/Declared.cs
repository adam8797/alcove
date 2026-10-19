using Alcove;
using Fixture.HostContracts;

namespace Fixture.Hosting.Declared;

public sealed class ChosenPlugin : IPlugin
{
    public void Register(IExtensionRegistrar registrar, IPluginContext context) =>
        registrar.Add<IDescriber>(() => new DeclaredDescriber("chosen"));
}

public sealed class OtherPlugin : IPlugin
{
    public void Register(IExtensionRegistrar registrar, IPluginContext context) =>
        registrar.Add<IDescriber>(() => new DeclaredDescriber("other"));
}

public sealed class DeclaredDescriber(string text) : IDescriber
{
    public string Describe() => text;
}

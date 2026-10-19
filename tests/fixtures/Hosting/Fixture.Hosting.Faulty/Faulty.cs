using Alcove;
using Fixture.HostContracts;

namespace Fixture.Hosting.Faulty;

public sealed class FaultyPlugin : IPlugin
{
    public void Register(IExtensionRegistrar registrar, IPluginContext context)
    {
        registrar.Add<IDescriber>(() => new FaultyDescriber());
        throw new InvalidOperationException("Faulty refuses to register.");
    }
}

public sealed class FaultyDescriber : IDescriber
{
    public string Describe() => "never listed";
}

public static class FaultyApi
{
    public static string Hello() => "hello from Faulty";
}

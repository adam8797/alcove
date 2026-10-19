using Alcove;
using Fixture.HostContracts;

namespace Fixture.Hosting.FaultyInit;

public sealed class FaultyInitPlugin : IPlugin
{
    public void Register(IExtensionRegistrar registrar, IPluginContext context) =>
        registrar.Add<IDescriber>(() => new FaultyInitDescriber());

    public async ValueTask InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        await Task.Yield();
        throw new IOException("FaultyInit can't initialize.");
    }
}

public sealed class FaultyInitDescriber : IDescriber
{
    public string Describe() => "never listed";
}

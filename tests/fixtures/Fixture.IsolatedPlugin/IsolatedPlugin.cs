using Alcove;
using Fixture.Contracts;

namespace Fixture.IsolatedPlugin;

public sealed class IsolatedPlugin : IPlugin
{
    public void Register(IExtensionRegistrar registrar, IPluginContext context)
        => registrar.Add<IGreeter>(() => new QuietGreeter());
}

public sealed class QuietGreeter : IGreeter
{
    public string Greet(string name) => $"hello, {name}";
}

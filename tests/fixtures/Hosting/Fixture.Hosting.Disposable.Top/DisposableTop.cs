using Alcove;
using Fixture.HostContracts;

namespace Fixture.Hosting.Disposable.Top;

public sealed class DisposableTopPlugin : IPlugin, IAsyncDisposable
{
    private IEventLog? _log;

    public void Register(IExtensionRegistrar registrar, IPluginContext context)
    {
        _log = (IEventLog?)context.Services.GetService(typeof(IEventLog));
        registrar.Add<IDescriber>(() => new DisposableTopDescriber(_log, "first"));
        registrar.Add<IGreeter>(() => new DisposableTopDescriber(_log, "second"));
    }

    public async ValueTask DisposeAsync()
    {
        await Task.Yield();
        _log?.Write("Top plugin disposed");
    }
}

public sealed class DisposableTopDescriber(IEventLog? log, string name) : IDescriber, IGreeter, IDisposable
{
    public string Describe() => "Top " + name;

    public string Greet(string who) => "Top " + name + " greets " + who;

    public void Dispose() => log?.Write("Top " + name + " singleton disposed");
}

using Alcove;
using Fixture.HostContracts;

namespace Fixture.Hosting.Disposable.Base;

public sealed class DisposableBasePlugin : IPlugin, IAsyncDisposable
{
    private IEventLog? _log;

    public void Register(IExtensionRegistrar registrar, IPluginContext context)
    {
        _log = (IEventLog?)context.Services.GetService(typeof(IEventLog));
        registrar.Add<IDescriber>(() => new DisposableBaseDescriber(_log, "first"));
        registrar.Add<IGreeter>(() => new DisposableBaseDescriber(_log, "second"));
    }

    public async ValueTask DisposeAsync()
    {
        await Task.Yield();
        _log?.Write("Base plugin disposed");
    }
}

public sealed class DisposableBaseDescriber(IEventLog? log, string name) : IDescriber, IGreeter, IDisposable
{
    public string Describe() => "Base " + name;

    public string Greet(string who) => "Base " + name + " greets " + who;

    public void Dispose() => log?.Write("Base " + name + " singleton disposed");
}

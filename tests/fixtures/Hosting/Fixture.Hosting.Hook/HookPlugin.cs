using Alcove;
using Fixture.HostContracts;

namespace Fixture.Hosting.Hook;

/// <summary>
/// Runs the host's <c>Func&lt;IPluginContext, CancellationToken, Task&gt;</c> service in InitializeAsync, and writes
/// "&lt;id&gt; disposed" to the host's <see cref="IEventLog"/> when disposed.
/// </summary>
public sealed class HookPlugin : IPlugin, IAsyncDisposable
{
    private IPluginContext? _context;

    public void Register(IExtensionRegistrar registrar, IPluginContext context)
    {
        _context = context;
        registrar.Add<IDescriber>(() => new HookDescriber());
    }

    public async ValueTask InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        if (context.Services.GetService(typeof(Func<IPluginContext, CancellationToken, Task>)) is Func<IPluginContext, CancellationToken, Task> hook)
        {
            await hook(context, ct);
        }
    }

    public ValueTask DisposeAsync()
    {
        (_context?.Services.GetService(typeof(IEventLog)) as IEventLog)?.Write($"{_context!.PluginId} disposed");
        return ValueTask.CompletedTask;
    }
}

public sealed class HookDescriber : IDescriber
{
    public string Describe() => "Hook";
}

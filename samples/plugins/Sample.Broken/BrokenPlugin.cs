using Alcove;
using ConsoleHost.Contracts;

namespace Sample.Broken;

public sealed class BrokenPlugin : IPlugin
{
    public void Register(IExtensionRegistrar registrar, IPluginContext context) =>
        registrar.Add<ICommand>(() => new NeverCommand(), new CommandAttribute { Name = "broken" });

    public ValueTask InitializeAsync(IPluginContext context, CancellationToken ct) =>
        throw new InvalidOperationException("The broken plugin can't find its configuration.");
}

public sealed class NeverCommand : ICommand
{
    public string Run() => "never runs";
}

using Alcove;
using Fixture.Contracts;

namespace Fixture.EntryPointPlugin;

// Shapes from docs/plugin-developer-guide.md §2.
public sealed class GreeterPlugin : IPlugin
{
    public void Register(IExtensionRegistrar registrar, IPluginContext context)
    {
        registrar.Add<IGreeter>(() => new EnglishGreeter(context.PluginId),
            new GreeterAttribute { Language = "en" });

        registrar.Add<IGreeter>(() => new EnglishGreeter(context.Version.ToNormalizedString()),
            new GreeterAttribute { Language = "en-transient" }, ExtensionLifetime.Transient);

        registrar.Add<IGreeter, FrenchGreeter>(new GreeterAttribute { Language = "fr" });
    }

    public async ValueTask InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        await Task.Yield();
        Directory.CreateDirectory(context.DataDirectory);
    }
}

public sealed class EnglishGreeter(string suffix) : IGreeter
{
    public string Greet(string name) => $"Hello, {name} ({suffix})";
}

public sealed class FrenchGreeter : IGreeter
{
    public string Greet(string name) => $"Bonjour, {name}";
}

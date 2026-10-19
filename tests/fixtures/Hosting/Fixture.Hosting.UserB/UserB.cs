using Alcove;
using Microsoft.Extensions.Logging;
using Fixture.HostContracts;

namespace Fixture.Hosting.UserB;

public sealed class UserBPlugin : IPlugin
{
    public void Register(IExtensionRegistrar registrar, IPluginContext context)
    {
        registrar.Add<IDescriber>(() => new UserBDescriber(context));
        registrar.Add<IGreeter>(() => new EnglishGreeter(), new GreeterAttribute { Language = "en" });
        registrar.Add<IGreeter, FrenchGreeter>(new GreeterAttribute { Language = "fr" }, ExtensionLifetime.Transient);
        registrar.Add<IGreeter, PlainGreeter>();
    }

    public async ValueTask InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        await Task.Yield();
        File.WriteAllText(Path.Combine(context.DataDirectory, "initialized.txt"), context.Version.ToNormalizedString());
        context.Logger.LogInformation("UserB initialized");
    }
}

public sealed class UserBDescriber(IPluginContext context) : IDescriber
{
    public string Describe() =>
        $"UserB uses {FixtureDep.Info.Describe()} and {FixtureDep.Info.NewIn2()}; services: {context.Services.GetService(typeof(IEventLog)) is not null}";
}

public sealed class EnglishGreeter : IGreeter
{
    public string Greet(string name) => $"Hello, {name}";
}

public sealed class FrenchGreeter : IGreeter
{
    public string Greet(string name) => $"Bonjour, {name}";
}

public sealed class PlainGreeter : IGreeter
{
    public string Greet(string name) => $"Hi, {name}";
}

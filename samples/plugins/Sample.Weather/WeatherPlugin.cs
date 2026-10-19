using Alcove;
using ConsoleHost.Contracts;
using Sample.Text;

namespace Sample.Weather;

public sealed class WeatherPlugin : IPlugin
{
    public void Register(IExtensionRegistrar registrar, IPluginContext context) =>
        registrar.Add<ICommand>(() => new ForecastCommand(context),
            new CommandAttribute { Name = "forecast", Description = "Tomorrow's weather (uses Sample.Text 2.0)" },
            ExtensionLifetime.Transient);

    public ValueTask InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        File.WriteAllText(Path.Combine(context.DataDirectory, "last-start.txt"), DateTime.UtcNow.ToString("O"));
        return ValueTask.CompletedTask;
    }
}

public sealed class ForecastCommand(IPluginContext context) : ICommand
{
    public string Run() => new TextBlock()
        .Add("Sunny, 21 C")
        .Add(TextInfo.Describe())
        .Add($"{context.PluginId} {context.Version} keeps data in {Path.GetFileName(context.DataDirectory)}/")
        .Framed();
}

using Alcove.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace Alcove.IntegrationTests.DependencyInjection;

/// <summary>
/// The dependency injection samples of docs/app-developer-guide.md §6 and docs/plugin-developer-guide.md §5, compiled
/// against the real add-on API: if a shape changes, this file stops compiling.
/// </summary>
public sealed class GuideSampleTests
{
    [Fact]
    public async Task AppGuide_AddPlugins_ThenTheCatalogFromTheContainer()
    {
        using var root = new Infrastructure.TempDirectory("alcove-guide");
        var services = new ServiceCollection();

        // --- app guide §6 ---
        services.AddPlugins(options => { options.Root = root.Path; /* same options */ });
        // later
        await using var provider = services.BuildServiceProvider();
        var catalog = provider.GetRequiredService<ExtensionCatalog>();
        // ---

        Assert.Same(provider.GetRequiredService<PluginHost>().Catalog, catalog);
    }

    [Fact]
    public void PluginGuide_AServicePlugin_RegistersServicesAndAnExtensionByType()
    {
        var plugin = new ChartsPlugin();
        var services = new ServiceCollection();
        var registrar = new Registrar();

        plugin.ConfigureServices(services);
        plugin.Register(registrar, null!);

        Assert.Equal(typeof(DefaultChartTheme), Assert.Single(services, d => d.ServiceType == typeof(IChartTheme)).ImplementationType);
        Assert.Equal((typeof(IToolWindow), typeof(ChartWindow), "Charts"), Assert.Single(registrar.Registrations));
    }

    [Fact]
    public void Autofac_TheSameShapes()
    {
        var builder = new global::Autofac.ContainerBuilder();
        Alcove.Autofac.PluginContainerBuilderExtensions.AddPlugins(builder, options => { /* same options */ });
        Alcove.Autofac.IServicePlugin plugin = new AutofacChartsPlugin();

        plugin.ConfigureContainer(builder);

        Assert.NotNull(builder);
    }

    // --- plugin guide §5 ---
    public sealed class ChartsPlugin : IPlugin, IServicePlugin
    {
        public void ConfigureServices(IServiceCollection services)
            => services.AddSingleton<IChartTheme, DefaultChartTheme>();

        public void Register(IExtensionRegistrar registrar, IPluginContext context)
            => registrar.Add<IToolWindow, ChartWindow>(new ToolWindowAttribute { Title = "Charts" });
    }
    // ---

    public sealed class AutofacChartsPlugin : IPlugin, Alcove.Autofac.IServicePlugin
    {
        public void ConfigureContainer(global::Autofac.ContainerBuilder builder)
            => global::Autofac.RegistrationExtensions.RegisterType<DefaultChartTheme>(builder).As<IChartTheme>().SingleInstance();

        public void Register(IExtensionRegistrar registrar, IPluginContext context)
            => registrar.Add<IToolWindow, ChartWindow>(new ToolWindowAttribute { Title = "Charts" });
    }

    public interface IToolWindow;

    public interface IChartTheme;

    public sealed class DefaultChartTheme : IChartTheme;

    public sealed class ToolWindowAttribute() : ExtensionAttribute(typeof(IToolWindow))
    {
        public string Title { get; init; } = "";
    }

    /// <summary>Gets <see cref="IChartTheme"/> from the cell's services through its constructor.</summary>
    public sealed class ChartWindow(IChartTheme theme) : IToolWindow
    {
        public IChartTheme Theme => theme;
    }

    private sealed class Registrar : IExtensionRegistrar
    {
        public List<(Type, Type, string)> Registrations { get; } = [];

        public void Add<T>(Func<T> factory, object? metadata = null, ExtensionLifetime lifetime = ExtensionLifetime.Singleton)
            where T : class => throw new NotSupportedException();

        public void Add<T, TImplementation>(object? metadata = null, ExtensionLifetime lifetime = ExtensionLifetime.Singleton)
            where T : class
            where TImplementation : class, T =>
            Registrations.Add((typeof(T), typeof(TImplementation), ((ToolWindowAttribute)metadata!).Title));
    }
}

using Microsoft.Extensions.DependencyInjection;

namespace Alcove.DependencyInjection;

/// <summary>
/// Implemented by a plugin's entry point (next to <see cref="IPlugin"/>) to register services in its cell's
/// service provider, when the host uses <c>Alcove.DependencyInjection</c>.
/// </summary>
/// <remarks>
/// <para>
/// Every plugin in a cell shares one provider, which falls back to the host's services. The plugin's extensions get
/// constructor injection from it, and <see cref="IPluginContext.Services"/> is that provider.
/// </para>
/// <para>
/// <see cref="ConfigureServices"/> is called once, before <see cref="IPlugin.Register"/>. A provider can't change
/// once built: installing a service plugin into a running cell that already has one loads it at the next startup.
/// </para>
/// </remarks>
public interface IServicePlugin
{
    /// <summary>Registers the plugin's services in its cell's service collection.</summary>
    /// <param name="services">The cell's service collection, shared with the other plugins of the cell.</param>
    void ConfigureServices(IServiceCollection services);
}

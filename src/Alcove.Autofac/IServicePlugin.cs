using Autofac;

namespace Alcove.Autofac;

/// <summary>
/// Implemented by a plugin's entry point (next to <see cref="IPlugin"/>) to register services in its cell's
/// lifetime scope, when the host uses <c>Alcove.Autofac</c>.
/// </summary>
/// <remarks>
/// <para>
/// Every plugin in a cell shares one child lifetime scope of the host's container, so the cell sees the host's
/// registrations too. The plugin's extensions get constructor injection from it, and
/// <see cref="IPluginContext.Services"/> resolves from it.
/// </para>
/// <para>
/// <see cref="ConfigureContainer"/> is called once, before <see cref="IPlugin.Register"/>. A scope can't take new
/// registrations once built: installing a service plugin into a running cell that already has one loads it at the
/// next startup.
/// </para>
/// </remarks>
public interface IServicePlugin
{
    /// <summary>Registers the plugin's services in its cell's lifetime scope.</summary>
    /// <param name="builder">The builder of the cell's scope, shared with the other plugins of the cell.</param>
    void ConfigureContainer(ContainerBuilder builder);
}

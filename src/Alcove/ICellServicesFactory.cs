namespace Alcove;

/// <summary>
/// Creates each cell's service provider: the extension point dependency injection add-ons plug into
/// (<see cref="PluginHostOptions.CellServicesFactory"/>). The core depends on no container; it only asks this
/// factory, once per cell, for the <see cref="IServiceProvider"/> the cell's plugins and extensions see.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="CreateServices"/> is called once per cell, when the cell's first plugin activates and before any
/// plugin in the cell registers extensions. By then the entry points of the cell's plugins being loaded are created,
/// so the provider can hold all their registrations. The provider is then <see cref="IPluginContext.Services"/> for
/// every plugin in the cell and <see cref="ExtensionActivationContext.Services"/> for their extensions.
/// </para>
/// <para>
/// A plugin registers services when its entry point implements one of <see cref="ServicePluginInterfaces"/>. The
/// host decides that from the plugin's metadata, without loading it, so the same answer holds at startup and for a
/// hot install, and an install that can't load leaves the running cells untouched.
/// </para>
/// <para>
/// A provider is immutable once built: a hot install of a plugin that registers services into a cell whose provider
/// exists is <see cref="InstallStatus.PendingRestart"/>, and nothing of it is loaded. Plugins that don't register
/// services can still hot-load into it. A new cell gets its provider when its first plugin loads.
/// </para>
/// <para>
/// The provider is built from every plugin being loaded into the cell with it, before any of them registers. A plugin
/// that is skipped afterwards (for example because a plugin it depends on faulted) may therefore still have
/// contributed its services to the cell's provider.
/// </para>
/// <para>
/// The host disposes each provider that implements <see cref="IAsyncDisposable"/> or <see cref="IDisposable"/>
/// when it's disposed: cell by cell in reverse load order, after the cell's extensions and plugins. If
/// <see cref="CreateServices"/> throws, every plugin of that cell activated afterwards faults.
/// </para>
/// </remarks>
public interface ICellServicesFactory
{
    /// <summary>
    /// The full names (as <see cref="Type.FullName"/>, e.g. <c>Alcove.DependencyInjection.IServicePlugin</c>) of the
    /// interfaces whose implementation by a plugin's entry point means the plugin registers services. Only those
    /// plugins are in <see cref="CellServicesContext.Plugins"/>.
    /// </summary>
    /// <remarks>
    /// The host reads them from metadata, without loading anything: the entry point implements one directly, or
    /// through base types and interfaces defined in the plugin's own assemblies, the other private assemblies of its
    /// cell, or the host's assemblies (for example an app SDK's <c>ServicePluginBase</c> that implements the add-on's
    /// interface). Attribute-only plugins have no entry point and register no services.
    /// </remarks>
    IReadOnlyCollection<string> ServicePluginInterfaces { get; }

    /// <summary>Creates a cell's service provider, falling back to the host's services.</summary>
    /// <param name="context">The cell, the host's services and the plugins that register services.</param>
    /// <returns>The cell's provider; the host disposes it.</returns>
    IServiceProvider CreateServices(CellServicesContext context);
}

/// <summary>What <see cref="ICellServicesFactory.CreateServices"/> builds a cell's provider from.</summary>
public sealed class CellServicesContext
{
    internal CellServicesContext(string cell, IServiceProvider hostServices, IReadOnlyList<CellServicesPlugin> plugins)
    {
        Cell = cell;
        HostServices = hostServices;
        Plugins = plugins;
    }

    /// <summary>The cell's name.</summary>
    public string Cell { get; }

    /// <summary>The host's services (<see cref="PluginHostOptions.Services"/>), which the cell's provider falls back to.</summary>
    public IServiceProvider HostServices { get; }

    /// <summary>
    /// The cell's plugins being loaded whose entry points implement one of
    /// <see cref="ICellServicesFactory.ServicePluginInterfaces"/>, in load order.
    /// </summary>
    public IReadOnlyList<CellServicesPlugin> Plugins { get; }
}

/// <summary>A plugin that registers services in its cell's provider.</summary>
public sealed class CellServicesPlugin
{
    internal CellServicesPlugin(PluginInfo plugin, IPlugin entryPoint)
    {
        Plugin = plugin;
        EntryPoint = entryPoint;
    }

    /// <summary>The plugin.</summary>
    public PluginInfo Plugin { get; }

    /// <summary>Its entry point instance (the same one whose <see cref="IPlugin.Register"/> runs next).</summary>
    public IPlugin EntryPoint { get; }
}

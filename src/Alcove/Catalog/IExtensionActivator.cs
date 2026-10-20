namespace Alcove.Catalog;

/// <summary>
/// Creates extension instances for the catalog. The default calls the registered factory, or a public
/// parameterless constructor for extensions registered by type. Dependency injection add-ons replace it
/// (<see cref="PluginHostOptions.Activator"/>) to get constructor injection.
/// </summary>
/// <remarks>
/// Called once for a singleton and on every request for a transient, on the thread asking for the
/// instance, inside the plugin's cell's contextual reflection scope. An exception thrown here faults the
/// plugin that registered the extension.
/// </remarks>
public interface IExtensionActivator
{
    /// <summary>Creates one instance of an extension.</summary>
    /// <param name="context">What to create, and for which plugin.</param>
    /// <returns>The instance; it must implement <see cref="ExtensionActivationContext.ExtensionPoint"/>.</returns>
    object CreateInstance(ExtensionActivationContext context);
}

/// <summary>What <see cref="IExtensionActivator.CreateInstance"/> is asked to create.</summary>
public sealed class ExtensionActivationContext
{
    internal ExtensionActivationContext(
        Type extensionPoint,
        Type? implementationType,
        Func<object>? factory,
        ExtensionLifetime lifetime,
        PluginInfo plugin,
        IServiceProvider services)
    {
        ExtensionPoint = extensionPoint;
        ImplementationType = implementationType;
        Factory = factory;
        Lifetime = lifetime;
        Plugin = plugin;
        Services = services;
    }

    /// <summary>The extension point, e.g. <c>IToolWindow</c>.</summary>
    public Type ExtensionPoint { get; }

    /// <summary>The type to create, for extensions registered by type or found by attribute; <see langword="null"/> when <see cref="Factory"/> is set.</summary>
    public Type? ImplementationType { get; }

    /// <summary>The factory the plugin registered, or <see langword="null"/> for extensions registered by type.</summary>
    public Func<object>? Factory { get; }

    /// <summary>The extension's lifetime.</summary>
    public ExtensionLifetime Lifetime { get; }

    /// <summary>The plugin that registered the extension.</summary>
    public PluginInfo Plugin { get; }

    /// <summary>The services the plugin sees (<see cref="IPluginContext.Services"/>).</summary>
    public IServiceProvider Services { get; }
}

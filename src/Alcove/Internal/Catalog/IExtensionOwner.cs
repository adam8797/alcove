using System.Runtime.Loader;

namespace Alcove.Internal.Catalog;

/// <summary>What an extension registration needs from the plugin that registered it.</summary>
internal interface IExtensionOwner
{
    /// <summary>The plugin.</summary>
    PluginInfo Plugin { get; }

    /// <summary>The services the plugin sees.</summary>
    IServiceProvider Services { get; }

    /// <summary>Creates the instances.</summary>
    IExtensionActivator Activator { get; }

    /// <summary>The plugin's cell, entered as the contextual reflection context while creating instances.</summary>
    AssemblyLoadContext? Cell { get; }

    /// <summary>Whether the host was disposed: no more instances are created.</summary>
    bool IsDisposed { get; }

    /// <summary>
    /// A singleton was created: the host disposes it, in reverse order. <see langword="false"/> if the host has already
    /// disposed the plugin's singletons: the caller then hands the instance to <see cref="DisposeRefused"/> (outside
    /// its lock) and throws <see cref="ObjectDisposedException"/>.
    /// </summary>
    bool SingletonCreated(ExtensionRegistration registration, object instance);

    /// <summary>Disposes a singleton that <see cref="SingletonCreated"/> refused. Doesn't throw.</summary>
    void DisposeRefused(ExtensionRegistration registration, object instance);

    /// <summary>Creating an instance threw: fault the plugin.</summary>
    void ExtensionFaulted(ExtensionRegistration registration, Exception exception);
}

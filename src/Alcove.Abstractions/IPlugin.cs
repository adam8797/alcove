namespace Alcove;

/// <summary>
/// Optional entry point of a plugin. A plugin without one is scanned for types marked with an
/// <see cref="ExtensionAttribute"/> instead.
/// </summary>
/// <remarks>
/// The entry point type needs a public parameterless constructor. If <see cref="Register"/> or
/// <see cref="InitializeAsync"/> throws, the plugin is marked Faulted and the other plugins keep loading.
/// </remarks>
public interface IPlugin
{
    /// <summary>
    /// Registers the plugin's extensions with the catalog. Called once, synchronously, when the plugin loads.
    /// </summary>
    /// <param name="registrar">Receives the plugin's extensions.</param>
    /// <param name="context">The plugin context: ID, version, data directory, logger and host services.</param>
    void Register(IExtensionRegistrar registrar, IPluginContext context);

    /// <summary>
    /// Optional asynchronous initialisation, awaited after <see cref="Register"/> in plugin dependency order.
    /// </summary>
    /// <param name="context">The plugin context.</param>
    /// <param name="cancellationToken">
    /// Cancels initialisation: cancelled when the app cancels the call that loads the plugin (<c>LoadAsync</c> or
    /// <c>InstallAsync</c>) while that call runs, or when the host is disposed. It stays valid after this method
    /// returns: from the end of that call on, only the host's disposal cancels it, so background work the plugin starts
    /// may observe it to stop when the host is disposed.
    /// </param>
    /// <returns>A task that completes when the plugin is initialised.</returns>
    /// <remarks>
    /// The host's disposal waits for this method to complete: one that ignores the cancellation delays disposal.
    /// </remarks>
    ValueTask InitializeAsync(IPluginContext context, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;
}

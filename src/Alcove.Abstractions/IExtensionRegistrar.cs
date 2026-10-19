namespace Alcove;

/// <summary>
/// Receives a plugin's extensions during <see cref="IPlugin.Register"/>.
/// </summary>
/// <remarks>
/// Metadata is any object the host can read without creating the extension, typically an instance of an
/// <see cref="ExtensionAttribute"/> subclass from the host's contracts package. The <c>lifetime</c>
/// argument decides the lifetime; <see cref="ExtensionAttribute.Lifetime"/> is only used for
/// attribute-discovered extensions.
/// </remarks>
public interface IExtensionRegistrar
{
    /// <summary>Registers an extension created by a factory.</summary>
    /// <typeparam name="T">The extension point, e.g. an interface from the host's contracts package.</typeparam>
    /// <param name="factory">Creates the extension. Called once for a singleton, on every request for a transient.</param>
    /// <param name="metadata">Extension metadata, or <see langword="null"/>.</param>
    /// <param name="lifetime">The extension lifetime.</param>
    void Add<T>(Func<T> factory, object? metadata = null, ExtensionLifetime lifetime = ExtensionLifetime.Singleton)
        where T : class;

    /// <summary>
    /// Registers an extension by type. It's created by the host's activator: a public parameterless
    /// constructor by default, or constructor injection with a dependency injection add-on.
    /// </summary>
    /// <typeparam name="T">The extension point.</typeparam>
    /// <typeparam name="TImplementation">The extension type.</typeparam>
    /// <param name="metadata">Extension metadata, or <see langword="null"/>.</param>
    /// <param name="lifetime">The extension lifetime.</param>
    void Add<T, TImplementation>(object? metadata = null, ExtensionLifetime lifetime = ExtensionLifetime.Singleton)
        where T : class
        where TImplementation : class, T;
}

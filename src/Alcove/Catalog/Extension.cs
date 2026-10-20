namespace Alcove.Catalog;

/// <summary>
/// One extension in the <see cref="ExtensionCatalog"/>: which plugin provides it, its metadata and its lifetime,
/// readable without creating it. <see cref="Extension{T}"/> adds the instance.
/// </summary>
public abstract class Extension
{
    private protected Extension(ExtensionRegistration registration)
    {
        Registration = registration;
    }

    /// <summary>The plugin that provides the extension.</summary>
    public PluginInfo Plugin => Registration.Owner.Plugin;

    /// <summary>The extension point it was registered for.</summary>
    public Type ExtensionPoint => Registration.ExtensionPoint;

    /// <summary>
    /// Its metadata: the object passed at registration, or the <see cref="ExtensionAttribute"/> instance for an
    /// attribute-discovered extension; <see langword="null"/> if none.
    /// </summary>
    public object? Metadata => Registration.Metadata;

    /// <summary>Its lifetime: use <see cref="Extension{T}.Value"/> for a singleton, <see cref="Extension{T}.CreateInstance"/> for a transient.</summary>
    public ExtensionLifetime Lifetime => Registration.Lifetime;

    internal ExtensionRegistration Registration { get; }

    /// <inheritdoc />
    public override string ToString() => $"{ExtensionPoint.Name} from {Plugin.Id} ({Lifetime})";
}

/// <summary>An extension of the extension point <typeparamref name="T"/>, created lazily.</summary>
/// <typeparam name="T">The extension point.</typeparam>
/// <remarks>
/// The lifetime is the plugin's choice and is enforced: <see cref="Value"/> only works for a singleton and
/// <see cref="CreateInstance"/> only for a transient; the other throws <see cref="InvalidOperationException"/>, so a
/// host never shares an instance meant to be created per use, or creates a second "singleton". A host that
/// doesn't care calls <see cref="GetInstance"/>. If the plugin throws while creating the instance,
/// the plugin is marked <see cref="PluginStatus.Faulted"/> and <see cref="PluginException"/> is thrown.
/// </remarks>
public class Extension<T> : Extension
    where T : class
{
    internal Extension(ExtensionRegistration registration)
        : base(registration)
    {
    }

    /// <summary>
    /// The singleton instance, created on first use (thread-safe; a failed creation is retried on the next access).
    /// Disposed with the host if it implements <see cref="IAsyncDisposable"/> or <see cref="IDisposable"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The extension is <see cref="ExtensionLifetime.Transient"/>; call <see cref="CreateInstance"/>.</exception>
    /// <exception cref="PluginException">The plugin threw while creating the instance; the plugin is now Faulted.</exception>
    /// <exception cref="ObjectDisposedException">The host was disposed.</exception>
    public T Value => (T)Registration.GetSingleton();

    /// <summary>A new instance of a transient extension. The caller owns (and disposes) it.</summary>
    /// <exception cref="InvalidOperationException">The extension is a <see cref="ExtensionLifetime.Singleton"/>; use <see cref="Value"/>.</exception>
    /// <exception cref="PluginException">The plugin threw while creating the instance; the plugin is now Faulted.</exception>
    /// <exception cref="ObjectDisposedException">The host was disposed.</exception>
    public T CreateInstance() => (T)Registration.CreateTransient();

    /// <summary>
    /// An instance whatever the lifetime: the shared <see cref="Value"/> of a singleton, or a new
    /// <see cref="CreateInstance"/> of a transient (which the caller then owns). For hosts that don't need to
    /// treat the two differently.
    /// </summary>
    /// <exception cref="PluginException">The plugin threw while creating the instance; the plugin is now Faulted.</exception>
    /// <exception cref="ObjectDisposedException">The host was disposed.</exception>
    public T GetInstance() => Lifetime == ExtensionLifetime.Singleton ? Value : CreateInstance();
}

/// <summary>An extension of <typeparamref name="T"/> whose metadata is a <typeparamref name="TMetadata"/>.</summary>
/// <typeparam name="T">The extension point.</typeparam>
/// <typeparam name="TMetadata">The metadata type, e.g. an <see cref="ExtensionAttribute"/> subclass from the host's contracts.</typeparam>
public sealed class Extension<T, TMetadata> : Extension<T>
    where T : class
{
    internal Extension(ExtensionRegistration registration, TMetadata metadata)
        : base(registration)
    {
        Metadata = metadata;
    }

    /// <summary>The typed metadata, readable without creating the extension.</summary>
    public new TMetadata Metadata { get; }
}

namespace Alcove.Catalog;

/// <summary>Collects one plugin's registrations during <see cref="IPlugin.Register"/> (or the attribute scan).</summary>
internal sealed class ExtensionRegistrar(IExtensionOwner owner) : IExtensionRegistrar
{
    private readonly List<ExtensionRegistration> _registrations = [];
    private bool _completed;

    /// <summary>The registered extensions, in registration order.</summary>
    public IReadOnlyList<Extension> Extensions => _registrations.Select(r => r.Extension).ToList();

    public void Add<T>(Func<T> factory, object? metadata = null, ExtensionLifetime lifetime = ExtensionLifetime.Singleton)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(factory);
        Register(new ExtensionRegistration(owner, typeof(T), null, () => factory(), metadata, lifetime));
    }

    public void Add<T, TImplementation>(object? metadata = null, ExtensionLifetime lifetime = ExtensionLifetime.Singleton)
        where T : class
        where TImplementation : class, T =>
        Add(typeof(T), typeof(TImplementation), metadata, lifetime);

    /// <summary>Registers an extension by type (registration by type, or an attribute-discovered extension).</summary>
    public void Add(Type extensionPoint, Type implementation, object? metadata, ExtensionLifetime lifetime)
    {
        if (implementation.IsAbstract || implementation.IsInterface || implementation.ContainsGenericParameters)
        {
            throw new ArgumentException($"{implementation} can't be an extension: it must be a non-abstract, non-generic class.", nameof(implementation));
        }

        if (!extensionPoint.IsAssignableFrom(implementation))
        {
            throw new ArgumentException($"{implementation} doesn't implement the extension point {extensionPoint}.", nameof(implementation));
        }

        Register(new ExtensionRegistration(owner, extensionPoint, implementation, null, metadata, lifetime));
    }

    /// <summary>Registration is over: later calls (e.g. from a stored registrar) throw.</summary>
    public void Complete() => _completed = true;

    private void Register(ExtensionRegistration registration)
    {
        if (_completed)
        {
            throw new InvalidOperationException($"Plugin '{owner.Plugin.Id}' registered an extension after Register returned; register everything inside Register.");
        }

        _registrations.Add(registration);
    }
}

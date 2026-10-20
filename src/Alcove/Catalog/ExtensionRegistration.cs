using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace Alcove.Catalog;

/// <summary>
/// One registered extension: what to create, how, and (for a singleton) the instance once created. Every
/// <see cref="Extension"/> view of it shares the instance.
/// </summary>
internal sealed class ExtensionRegistration
{
    private readonly Lock _gate = new();
    private object? _instance;
    private int _creatingThread;   // guarded by _gate: the thread creating the singleton, or 0

    public ExtensionRegistration(
        IExtensionOwner owner,
        Type extensionPoint,
        Type? implementationType,
        Func<object>? factory,
        object? metadata,
        ExtensionLifetime lifetime)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(extensionPoint);
        if (implementationType is null == factory is null)
        {
            throw new ArgumentException("Exactly one of an implementation type and a factory is needed.");
        }

        if (!Enum.IsDefined(lifetime))
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime), lifetime, "Unknown extension lifetime.");
        }

        if (extensionPoint.IsValueType || extensionPoint.ContainsGenericParameters)
        {
            throw new ArgumentException($"The extension point {extensionPoint} must be a closed class or interface type.", nameof(extensionPoint));
        }

        Owner = owner;
        ExtensionPoint = extensionPoint;
        ImplementationType = implementationType;
        Factory = factory;
        Metadata = metadata;
        Lifetime = lifetime;
        Extension = (Extension)Activator.CreateInstance(
            typeof(Extension<>).MakeGenericType(extensionPoint),
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: [this],
            culture: null)!;
    }

    public IExtensionOwner Owner { get; }

    public Type ExtensionPoint { get; }

    public Type? ImplementationType { get; }

    public Func<object>? Factory { get; }

    public object? Metadata { get; }

    public ExtensionLifetime Lifetime { get; }

    /// <summary>The <see cref="Extension{T}"/> of <see cref="ExtensionPoint"/> for this registration.</summary>
    public Extension Extension { get; }

    /// <summary>The singleton, if it was created.</summary>
    public object? CreatedInstance => Volatile.Read(ref _instance);

    /// <remarks>
    /// The singleton's constructor runs under this registration's lock, so it's created once; a constructor that
    /// needs another singleton whose constructor (on another thread) needs this one deadlocks, as in any DI
    /// container. Faults are reported (<see cref="PluginHost.PluginFaulted"/>) after the lock is released. A singleton
    /// whose construction finishes after the host disposed the plugin's singletons is disposed at once, and
    /// <see cref="ObjectDisposedException"/> is thrown.
    /// </remarks>
    public object GetSingleton()
    {
        if (Lifetime != ExtensionLifetime.Singleton)
        {
            throw new InvalidOperationException(
                $"The {ExtensionPoint.Name} extension of plugin '{Owner.Plugin.Id}' is transient: call CreateInstance() for a new instance each time.");
        }

        if (Volatile.Read(ref _instance) is { } existing)
        {
            ObjectDisposedException.ThrowIf(Owner.IsDisposed, typeof(PluginHost));
            return existing;
        }

        Exception? fault;
        object? refused;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(Owner.IsDisposed, typeof(PluginHost));
            if (_instance is { } existingInstance)
            {
                return existingInstance;
            }

            if (_creatingThread == Environment.CurrentManagedThreadId)
            {
                throw new InvalidOperationException(
                    $"The {ExtensionPoint.Name} singleton of plugin '{Owner.Plugin.Id}' needs itself while being created.");
            }

            // A failure isn't cached: the next access tries again.
            _creatingThread = Environment.CurrentManagedThreadId;
            bool created;
            object? instance;
            try
            {
                created = TryCreate(out instance, out fault);
            }
            finally
            {
                _creatingThread = 0;
            }

            if (created)
            {
                if (Owner.SingletonCreated(this, instance!))
                {
                    Volatile.Write(ref _instance, instance);
                    return instance!;
                }

                refused = instance;
            }
            else
            {
                refused = null;
            }
        }

        // Outside the lock: disposal and the fault handlers are plugin and app code.
        if (refused is not null)
        {
            Owner.DisposeRefused(this, refused);
            throw new ObjectDisposedException(typeof(PluginHost).FullName, $"The host was disposed while the {ExtensionPoint.Name} singleton of plugin '{Owner.Plugin.Id}' was being created; it was disposed.");
        }

        throw Faulted(fault!);
    }

    public object CreateTransient()
    {
        if (Lifetime != ExtensionLifetime.Transient)
        {
            throw new InvalidOperationException(
                $"The {ExtensionPoint.Name} extension of plugin '{Owner.Plugin.Id}' is a singleton: use Value for its one shared instance.");
        }

        ObjectDisposedException.ThrowIf(Owner.IsDisposed, typeof(PluginHost));
        return TryCreate(out var instance, out var fault) ? instance : throw Faulted(fault);
    }

    /// <summary>Creates an instance; on failure returns the cause (nothing reported yet).</summary>
    private bool TryCreate([NotNullWhen(true)] out object? instance, [NotNullWhen(false)] out Exception? fault)
    {
        var context = new ExtensionActivationContext(ExtensionPoint, ImplementationType, Factory, Lifetime, Owner.Plugin, Owner.Services);
        try
        {
            using (Owner.Cell?.EnterContextualReflection())
            {
                instance = Owner.Activator.CreateInstance(context);
            }

            if (instance is null)
            {
                throw new InvalidOperationException($"The activator returned null for the {ExtensionPoint.Name} extension.");
            }

            if (!ExtensionPoint.IsInstanceOfType(instance))
            {
                throw new InvalidCastException($"The {ExtensionPoint.Name} extension created a {instance.GetType()}, which doesn't implement {ExtensionPoint}.");
            }

            fault = null;
            return true;
        }
        catch (Exception ex)
        {
            instance = null;
            fault = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
            return false;
        }
    }

    /// <summary>Reports a creation failure (the plugin faults) and returns the exception to throw.</summary>
    private PluginException Faulted(Exception cause)
    {
        Owner.ExtensionFaulted(this, cause);
        return new PluginException(
            $"Plugin '{Owner.Plugin.Id}' couldn't create its {ExtensionPoint.Name} extension: {cause.Message}",
            Owner.Plugin.Id,
            cause);
    }
}

using System.Runtime.Loader;
using Alcove.Internal.Catalog;

namespace Alcove.Internal.Loading;

/// <summary>A plugin being activated or loaded in this host: its cell, entry point instance and created singletons.</summary>
/// <remarks>
/// Disposal takes what it disposes (<see cref="CloseSingletons"/>, <see cref="TakeInstance"/>), so a later sweep
/// (after an operation that disposal didn't wait for) disposes only what was created since, never twice.
/// </remarks>
internal sealed class PluginRuntime(PluginHost host, PluginInfo plugin, IServiceProvider services, IExtensionActivator activator) : IExtensionOwner
{
    private readonly Lock _gate = new();
    private readonly List<object> _singletons = [];
    private bool _singletonsClosed;   // guarded by _gate: disposal took the singletons; later ones are disposed at once
    private IPlugin? _instance;   // guarded by _gate
    private bool _instanceTaken;   // guarded by _gate

    public PluginInfo Plugin { get; } = plugin;

    /// <summary>The services the plugin sees: the host's, or its cell's provider once created.</summary>
    public IServiceProvider Services { get; set; } = services;

    public IExtensionActivator Activator { get; } = activator;

    public AssemblyLoadContext? Cell { get; set; }

    public bool IsDisposed => host.IsDisposed;

    /// <summary>The entry point instance, if the plugin has one (set once).</summary>
    public IPlugin? Instance
    {
        get
        {
            lock (_gate)
            {
                return _instance;
            }
        }

        set
        {
            lock (_gate)
            {
                _instance = value;
            }
        }
    }

    /// <summary>
    /// For disposal: the singletons created so far, in creation order, that weren't taken before. From now on a new
    /// singleton is refused (<see cref="SingletonCreated"/>).
    /// </summary>
    public List<object> CloseSingletons()
    {
        lock (_gate)
        {
            _singletonsClosed = true;
            var singletons = _singletons.ToList();
            _singletons.Clear();
            return singletons;
        }
    }

    /// <summary>For disposal: the entry point, unless there is none yet or it was taken before.</summary>
    public IPlugin? TakeInstance()
    {
        lock (_gate)
        {
            if (_instance is null || _instanceTaken)
            {
                return null;
            }

            _instanceTaken = true;
            return _instance;
        }
    }

    public bool SingletonCreated(ExtensionRegistration registration, object instance)
    {
        lock (_gate)
        {
            if (_singletonsClosed)
            {
                return false;
            }

            _singletons.Add(instance);
            return true;
        }
    }

    public void DisposeRefused(ExtensionRegistration registration, object instance) =>
        host.DisposeRefusedSingleton(instance, Plugin.Id);

    public void ExtensionFaulted(ExtensionRegistration registration, Exception exception) =>
        host.ReportFault(this, $"creating its {registration.ExtensionPoint.Name} extension", exception);
}

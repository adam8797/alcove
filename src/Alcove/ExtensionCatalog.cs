using Alcove.Internal.Catalog;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Alcove;

/// <summary>
/// Every extension of every loaded plugin, by extension point. Extensions are listed with their metadata and
/// created lazily. Nothing is removed during a run.
/// </summary>
/// <remarks>
/// <para>
/// An extension is listed under the exact extension point it was registered for (<c>registrar.Add&lt;IToolWindow&gt;</c>,
/// or <see cref="ExtensionAttribute.ExtensionPoint"/>), not under its base types. A plugin's extensions are added
/// together, after its <see cref="IPlugin.InitializeAsync"/> succeeded, in load order.
/// </para>
/// <para>
/// Threading: <see cref="Observe{T}"/> callbacks and <see cref="ExtensionsAdded"/> run on the thread that loaded
/// the plugin. Callbacks of one subscription never run concurrently and each extension is delivered exactly
/// once: a thread that adds extensions while another thread is delivering to the same subscription doesn't wait for
/// it, and that thread delivers them after its current callback (so a callback may wait for another thread that adds
/// extensions, e.g. one that marshals to the UI thread). An exception from a callback or handler during a load is
/// logged and doesn't stop the load.
/// </para>
/// </remarks>
public sealed class ExtensionCatalog
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Type, List<Extension>> _byExtensionPoint = [];
    private readonly List<ISubscription> _subscriptions = [];
    private readonly ILogger _logger;

    internal ExtensionCatalog(ILoggerFactory? loggerFactory = null)
    {
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger("Alcove.Catalog");
    }

    /// <summary>Raised after a plugin's extensions were added (at load, or by a hot install), on the loading thread.</summary>
    public event EventHandler<ExtensionsAddedEventArgs>? ExtensionsAdded;

    private interface ISubscription
    {
        Type ExtensionPoint { get; }

        void Deliver(bool rethrow);
    }

    /// <summary>Every extension of <typeparamref name="T"/>, in load order. Nothing is created.</summary>
    /// <typeparam name="T">The extension point.</typeparam>
    public IReadOnlyList<Extension<T>> GetAll<T>()
        where T : class
    {
        lock (_gate)
        {
            return _byExtensionPoint.TryGetValue(typeof(T), out var list) ? list.Cast<Extension<T>>().ToList() : [];
        }
    }

    /// <summary>Every extension of <typeparamref name="T"/> whose metadata is a <typeparamref name="TMetadata"/>, in load order. Nothing is created.</summary>
    /// <typeparam name="T">The extension point.</typeparam>
    /// <typeparam name="TMetadata">The metadata type; extensions with other (or no) metadata are left out.</typeparam>
    public IReadOnlyList<Extension<T, TMetadata>> GetAll<T, TMetadata>()
        where T : class
    {
        lock (_gate)
        {
            if (!_byExtensionPoint.TryGetValue(typeof(T), out var list))
            {
                return [];
            }

            var result = new List<Extension<T, TMetadata>>();
            foreach (var extension in list)
            {
                if (extension.Metadata is TMetadata metadata)
                {
                    result.Add(new Extension<T, TMetadata>(extension.Registration, metadata));
                }
            }

            return result;
        }
    }

    /// <summary>
    /// Calls <paramref name="onAdded"/> for every extension of <typeparamref name="T"/> already in the catalog
    /// (on this thread, before returning), then for each one added later, until the subscription is disposed.
    /// No extension is missed or delivered twice, even while plugins are being added concurrently.
    /// </summary>
    /// <typeparam name="T">The extension point.</typeparam>
    /// <param name="onAdded">Called once per extension.</param>
    /// <returns>
    /// Disposing it ends the subscription: no callback starts after <see cref="IDisposable.Dispose"/> returns. Dispose
    /// doesn't wait for a callback another thread is already delivering (so it can't deadlock with one).
    /// </returns>
    /// <remarks>An exception from <paramref name="onAdded"/> for an existing extension ends the subscription and is rethrown.</remarks>
    public IDisposable Observe<T>(Action<Extension<T>> onAdded)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(onAdded);
        var subscription = new Subscription<T>(this, onAdded);
        lock (_gate)
        {
            _subscriptions.Add(subscription);
            subscription.ClaimDelivery();   // so the existing extensions are delivered on this thread
        }

        try
        {
            subscription.Deliver(rethrow: true);
        }
        catch
        {
            subscription.Dispose();
            throw;
        }

        return subscription;
    }

    /// <summary>Adds one plugin's extensions, then notifies subscriptions and <see cref="ExtensionsAdded"/> on this thread.</summary>
    internal void Add(PluginInfo plugin, IReadOnlyList<Extension> extensions)
    {
        if (extensions.Count == 0)
        {
            return;
        }

        List<ISubscription> subscriptions;
        lock (_gate)
        {
            foreach (var extension in extensions)
            {
                if (!_byExtensionPoint.TryGetValue(extension.ExtensionPoint, out var list))
                {
                    _byExtensionPoint[extension.ExtensionPoint] = list = [];
                }

                list.Add(extension);
            }

            var points = extensions.Select(e => e.ExtensionPoint).ToHashSet();
            subscriptions = _subscriptions.Where(s => points.Contains(s.ExtensionPoint)).ToList();
        }

        CatalogLog.ExtensionsAdded(_logger, extensions.Count, plugin.Id);
        foreach (var subscription in subscriptions)
        {
            subscription.Deliver(rethrow: false);
        }

        var handlers = ExtensionsAdded;
        if (handlers is null)
        {
            return;
        }

        var args = new ExtensionsAddedEventArgs(plugin, extensions);
        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<ExtensionsAddedEventArgs>>())
        {
            try
            {
                handler(this, args);
            }
            catch (Exception ex)
            {
                CatalogLog.HandlerFailed(_logger, ex, plugin.Id);
            }
        }
    }

    private sealed class Subscription<T>(ExtensionCatalog catalog, Action<Extension<T>> callback) : ISubscription, IDisposable
        where T : class
    {
        // The thread delivering to this subscription, or 0 (guarded by catalog._gate), so callbacks never overlap. A
        // thread that finds another one delivering returns at once: the deliverer checks for more extensions (under
        // the gate) before it stops, so it delivers them; waiting for it could deadlock with a callback that waits for
        // this thread. The delivering thread itself may deliver again (a callback that loads a plugin delivers that
        // plugin's extensions itself).
        private int _deliveringThread;
        private int _delivered;   // guarded by catalog._gate
        private bool _disposed;   // guarded by catalog._gate

        public Type ExtensionPoint => typeof(T);

        /// <summary>Makes the current thread the delivering one (under catalog._gate, for a new subscription).</summary>
        public void ClaimDelivery() => _deliveringThread = Environment.CurrentManagedThreadId;

        public void Deliver(bool rethrow)
        {
            var thread = Environment.CurrentManagedThreadId;
            bool owner;
            lock (catalog._gate)
            {
                if (_deliveringThread == 0)
                {
                    _deliveringThread = thread;
                    owner = true;
                }
                else if (_deliveringThread != thread)
                {
                    return;   // the delivering thread delivers what was added
                }
                else
                {
                    // Observe claimed it for this call; otherwise a callback on this thread added a plugin (nested),
                    // and the outer delivery releases it.
                    owner = rethrow;
                }
            }

            var finished = false;
            try
            {
                while (true)
                {
                    Extension<T> next;
                    lock (catalog._gate)
                    {
                        if (_disposed
                            || !catalog._byExtensionPoint.TryGetValue(typeof(T), out var list)
                            || _delivered >= list.Count)
                        {
                            if (owner)
                            {
                                _deliveringThread = 0;   // atomically with the check: a later Add delivers itself
                            }

                            finished = true;
                            return;
                        }

                        next = (Extension<T>)list[_delivered++];
                    }

                    try
                    {
                        callback(next);
                    }
                    catch (Exception ex) when (!rethrow)
                    {
                        CatalogLog.ObserverFailed(catalog._logger, ex, typeof(T).Name, next.Plugin.Id);
                    }
                }
            }
            finally
            {
                if (owner && !finished)
                {
                    lock (catalog._gate)
                    {
                        // A callback threw for an existing extension: Observe rethrows and the subscription ends. End
                        // it before releasing the claim, so no concurrent Add delivers to it in between.
                        if (rethrow)
                        {
                            _disposed = true;
                            catalog._subscriptions.Remove(this);
                        }

                        _deliveringThread = 0;
                    }
                }
            }
        }

        public void Dispose()
        {
            // Doesn't wait for a callback running on another thread: that could deadlock with it (e.g. a callback that
            // marshals to the thread disposing). Deliver checks _disposed before each callback.
            lock (catalog._gate)
            {
                _disposed = true;
                catalog._subscriptions.Remove(this);
            }
        }
    }
}

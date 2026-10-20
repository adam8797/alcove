using System.Windows.Threading;
using Alcove.Catalog;

namespace Alcove.Wpf.Threading;

/// <summary>
/// An <see cref="ExtensionCatalog"/> whose callbacks run on a <see cref="System.Windows.Threading.Dispatcher"/>'s
/// thread (the UI thread), wherever the plugin was loaded: <c>host.Catalog.OnDispatcher(app.Dispatcher)</c>.
/// </summary>
/// <remarks>
/// <para>
/// A callback raised on the dispatcher's thread runs at once (so <see cref="Observe{T}"/> called on the UI thread has
/// delivered the existing extensions when it returns, as the catalog does). One raised on another thread, e.g. by a hot
/// install that continued on the thread pool, is queued with <see cref="Dispatcher.BeginInvoke(Delegate, object[])"/>
/// and runs later: the loading thread never waits for the UI thread, so a UI thread that waits for the install can't
/// deadlock with it. Each subscription keeps its order, and no extension is delivered twice or after it was disposed.
/// </para>
/// <para>
/// An exception from a queued callback surfaces on the dispatcher (<see cref="Dispatcher.UnhandledException"/>); one
/// from a callback run at once is handled by the catalog (logged; an existing extension's ends the subscription).
/// </para>
/// </remarks>
public sealed class DispatcherExtensionCatalog
{
    private readonly Lock _gate = new();
    private EventHandler<ExtensionsAddedEventArgs>? _extensionsAdded;
    private Subscription<ExtensionsAddedEventArgs>? _extensionsAddedSubscription;

    /// <summary>Wraps a catalog.</summary>
    /// <param name="catalog">The host's catalog.</param>
    /// <param name="dispatcher">The dispatcher the callbacks run on.</param>
    public DispatcherExtensionCatalog(ExtensionCatalog catalog, Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(dispatcher);
        Catalog = catalog;
        Dispatcher = dispatcher;
    }

    /// <summary>The wrapped catalog.</summary>
    public ExtensionCatalog Catalog { get; }

    /// <summary>The dispatcher the callbacks run on.</summary>
    public Dispatcher Dispatcher { get; }

    /// <summary>
    /// Raised on the dispatcher's thread after a plugin's extensions were added (see <see cref="ExtensionCatalog.ExtensionsAdded"/>);
    /// the sender is the wrapped <see cref="Catalog"/>.
    /// </summary>
    public event EventHandler<ExtensionsAddedEventArgs>? ExtensionsAdded
    {
        add
        {
            lock (_gate)
            {
                _extensionsAdded += value;
                if (_extensionsAdded is not null && _extensionsAddedSubscription is null)
                {
                    var subscription = new Subscription<ExtensionsAddedEventArgs>(Dispatcher, e => Volatile.Read(ref _extensionsAdded)?.Invoke(Catalog, e));
                    EventHandler<ExtensionsAddedEventArgs> handler = (_, e) => subscription.Deliver(e);
                    Catalog.ExtensionsAdded += handler;
                    subscription.SetInner(new Unsubscriber(() => Catalog.ExtensionsAdded -= handler));
                    _extensionsAddedSubscription = subscription;
                }
            }
        }

        remove
        {
            lock (_gate)
            {
                _extensionsAdded -= value;
                if (_extensionsAdded is null && _extensionsAddedSubscription is { } subscription)
                {
                    _extensionsAddedSubscription = null;
                    subscription.Dispose();
                }
            }
        }
    }

    /// <summary>Every extension of <typeparamref name="T"/>, in load order (<see cref="ExtensionCatalog.GetAll{T}"/>).</summary>
    /// <typeparam name="T">The extension point.</typeparam>
    /// <returns>The extensions; nothing is created.</returns>
    public IReadOnlyList<Extension<T>> GetAll<T>()
        where T : class => Catalog.GetAll<T>();

    /// <summary>Every extension of <typeparamref name="T"/> with <typeparamref name="TMetadata"/> metadata (<see cref="ExtensionCatalog.GetAll{T, TMetadata}"/>).</summary>
    /// <typeparam name="T">The extension point.</typeparam>
    /// <typeparam name="TMetadata">The metadata type.</typeparam>
    /// <returns>The extensions; nothing is created.</returns>
    public IReadOnlyList<Extension<T, TMetadata>> GetAll<T, TMetadata>()
        where T : class => Catalog.GetAll<T, TMetadata>();

    /// <summary>
    /// Calls <paramref name="onAdded"/> on the dispatcher's thread for every extension of <typeparamref name="T"/> already in
    /// the catalog, then for each one added later, until the subscription is disposed (<see cref="ExtensionCatalog.Observe{T}"/>).
    /// </summary>
    /// <typeparam name="T">The extension point.</typeparam>
    /// <param name="onAdded">Called once per extension, on the dispatcher's thread.</param>
    /// <returns>Disposing it ends the subscription; callbacks still queued don't run.</returns>
    public IDisposable Observe<T>(Action<Extension<T>> onAdded)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(onAdded);
        var subscription = new Subscription<Extension<T>>(Dispatcher, onAdded);
        try
        {
            subscription.SetInner(Catalog.Observe<T>(subscription.Deliver));
        }
        catch
        {
            subscription.Dispose();
            throw;
        }

        return subscription;
    }

    /// <summary>One subscription's ordered queue of deliveries for the dispatcher's thread.</summary>
    private sealed class Subscription<TItem>(Dispatcher dispatcher, Action<TItem> callback) : IDisposable
    {
        private readonly Lock _gate = new();
        private readonly Queue<TItem> _queue = new();
        private IDisposable? _inner;
        private bool _scheduled;
        private bool _draining;
        private bool _disposed;

        /// <summary>Sets the catalog subscription this one ends; disposes it at once if this one already ended.</summary>
        public void SetInner(IDisposable inner)
        {
            bool disposed;
            lock (_gate)
            {
                _inner = inner;
                disposed = _disposed;
            }

            if (disposed)
            {
                inner.Dispose();
            }
        }

        public void Deliver(TItem item)
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _queue.Enqueue(item);
                if (!dispatcher.CheckAccess())
                {
                    Schedule();
                    return;
                }
            }

            Drain();
        }

        public void Dispose()
        {
            IDisposable? inner;
            lock (_gate)
            {
                _disposed = true;
                _queue.Clear();
                inner = _inner;
            }

            inner?.Dispose();
        }

        /// <summary>
        /// Runs queued callbacks in order, on the dispatcher's thread. Not re-entrant: a callback that pumps messages
        /// (e.g. shows a modal dialog) doesn't get the next one until it returns.
        /// </summary>
        private void Drain()
        {
            lock (_gate)
            {
                if (_draining)
                {
                    return;
                }

                _draining = true;
            }

            try
            {
                while (true)
                {
                    TItem item;
                    lock (_gate)
                    {
                        if (_disposed || !_queue.TryDequeue(out item!))
                        {
                            return;
                        }
                    }

                    callback(item);
                }
            }
            finally
            {
                lock (_gate)
                {
                    _draining = false;

                    // After an exception the rest still runs, later; the exception goes to whoever raised this callback.
                    if (!_disposed && _queue.Count > 0)
                    {
                        Schedule();
                    }
                }
            }
        }

        /// <summary>Queues a drain on the dispatcher unless one is queued. Under <see cref="_gate"/>.</summary>
        private void Schedule()
        {
            if (_scheduled)
            {
                return;
            }

            _scheduled = true;
            dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
            {
                lock (_gate)
                {
                    _scheduled = false;
                }

                Drain();
            }));
        }
    }

    private sealed class Unsubscriber(Action unsubscribe) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                unsubscribe();
            }
        }
    }
}

/// <summary>Creates <see cref="DispatcherExtensionCatalog"/>s.</summary>
public static class DispatcherExtensionCatalogExtensions
{
    /// <summary>The catalog with its callbacks marshalled to <paramref name="dispatcher"/>'s thread.</summary>
    /// <param name="catalog">The host's catalog.</param>
    /// <param name="dispatcher">The UI thread's dispatcher, e.g. <c>Application.Current.Dispatcher</c>.</param>
    /// <returns>The wrapper.</returns>
    public static DispatcherExtensionCatalog OnDispatcher(this ExtensionCatalog catalog, Dispatcher dispatcher) => new(catalog, dispatcher);
}

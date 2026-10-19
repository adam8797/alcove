using Alcove.Internal.Cells;
using Alcove.Internal.Hosting;
using Alcove.Internal.Metadata;
using Microsoft.Extensions.Logging;

namespace Alcove.Internal.Loading;

/// <summary>
/// The cells' service providers of one host (<see cref="ICellServicesFactory"/>). Without a factory every plugin sees
/// the host's services. With one, a cell's first activation prepares every plugin of the batch being loaded into that
/// cell (assemblies and entry point), then asks the factory for the cell's provider before any of them registers.
/// </summary>
internal sealed class CellServiceProviders(ICellServicesFactory? factory, IServiceProvider hostServices, ILogger logger, HostAssemblyFiles? hostFiles = null)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, CellServices> _cells = new(StringComparer.Ordinal);
    private readonly List<CellServices> _created = [];

    /// <summary>Whether cells get their own service providers (an <see cref="ICellServicesFactory"/> is set).</summary>
    public bool HasFactory => factory is not null;

    /// <summary>Whether a cell's provider was created (or its creation failed).</summary>
    public bool IsCreated(string cell)
    {
        lock (_gate)
        {
            return _cells.TryGetValue(cell, out var services) && services.IsCreated;
        }
    }

    /// <summary>
    /// Prepares a plugin that is about to activate, and returns the services it sees. Throws (for activation to
    /// report as a fault) when the plugin's cell couldn't create its provider.
    /// </summary>
    /// <param name="planned">The plugin.</param>
    /// <param name="cell">Its cell.</param>
    /// <param name="plan">Its cell's plan.</param>
    /// <param name="batch">The plugins being loaded with it, in load order (all of <c>LoadAsync</c>, or an install's new plugins).</param>
    public (PreparedPlugin Prepared, Func<IServiceProvider> Services) Prepare(
        PlannedPlugin planned, Cell cell, CellPlan plan, IReadOnlyList<(PlannedPlugin Plugin, PluginInfo Info)> batch)
    {
        if (factory is null)
        {
            return (PreparedPlugin.Prepare(planned, cell, plan), () => hostServices);
        }

        CellServices services;
        lock (_gate)
        {
            if (!_cells.TryGetValue(planned.Cell!, out services!))
            {
                services = new CellServices(planned.Cell!);
                _cells[services.Cell] = services;
                _created.Add(services);
            }
        }

        if (services.TakePrepared(planned.Id) is { } prepared)
        {
            return (prepared, services.GetProvider);
        }

        if (services.IsCreated)
        {
            // A plugin hot-loaded into a cell with a provider; it registers no services (CompleteInstall checked).
            return (PreparedPlugin.Prepare(planned, cell, plan), services.GetProvider);
        }

        // The cell's first activation: prepare its plugins of this batch, then create the provider from them.
        // (Plugins of the batch skipped later, e.g. because a dependency faulted, still contributed their services.)
        var mates = batch.Where(m => m.Plugin == planned
            || (string.Equals(m.Plugin.Cell, planned.Cell, StringComparison.Ordinal) && m.Info.Status == PluginStatus.NotLoaded));
        var contributors = new List<CellServicesPlugin>();
        foreach (var (mate, info) in mates)
        {
            var preparedMate = PreparedPlugin.Prepare(mate, cell, plan);
            services.AddPrepared(mate.Id, preparedMate);
            if (preparedMate.EntryPoint is { } entryPoint && RegistersServices(mate, plan))
            {
                contributors.Add(new CellServicesPlugin(info, entryPoint));
            }
        }

        try
        {
            IServiceProvider provider;
            using (cell.EnterContextualReflection())
            {
                provider = factory.CreateServices(new CellServicesContext(services.Cell, hostServices, contributors))
                    ?? throw new InvalidOperationException($"{factory.GetType()}.CreateServices returned null for cell '{services.Cell}'.");
            }

            services.SetProvider(provider);
            CellServicesLog.Created(logger, services.Cell, contributors.Count);
        }
        catch (Exception ex)
        {
            services.SetFault(ex);
            CellServicesLog.CreateFailed(logger, ex, services.Cell);
        }

        return (services.TakePrepared(planned.Id) ?? PreparedPlugin.Prepare(planned, cell, plan), services.GetProvider);
    }

    /// <summary>
    /// Whether a plugin registers services: its entry point implements one of the factory's
    /// <see cref="ICellServicesFactory.ServicePluginInterfaces"/>. Read from metadata, so nothing is loaded; the one
    /// answer used both when a cell's provider is created and when a hot install decides whether it can load.
    /// </summary>
    /// <param name="planned">The plugin.</param>
    /// <param name="plan">Its cell's plan, which holds the files of its assemblies and of the cell's other private assemblies.</param>
    /// <remarks>
    /// Base types and interfaces are followed into the plugin's assemblies, the cell's other private assemblies and
    /// the host's assemblies (so an app SDK's <c>ServicePluginBase : IServicePlugin</c> counts), by metadata only.
    /// </remarks>
    public bool RegistersServices(PlannedPlugin planned, CellPlan plan)
    {
        if (factory is null || planned.EntryPoint is not { } entryPoint)
        {
            return false;
        }

        var own = plan.Private.Values
            .Where(a => string.Equals(a.PackageId, planned.Id, StringComparison.OrdinalIgnoreCase))
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Select(a => a.Path);
        return EntryPointInterfaceDetector.Implements(entryPoint, own, factory.ServicePluginInterfaces, Resolve);

        // As the cell answers: its private assemblies, else the host's (host-owned or host-shared). Assemblies another
        // cell provides aren't followed.
        string? Resolve(string name) =>
            plan.Private.TryGetValue(name, out var asset) ? asset.Path
            : plan.Provided.ContainsKey(name) ? null
            : hostFiles?.Find(name, isHost: plan.HostOwned.Contains(name) || plan.HostShared.Contains(name));
    }

    /// <summary>
    /// Why a hot install can't load its new plugins: a plugin that registers services joining a cell whose provider
    /// already exists (providers are immutable). Decides without loading anything.
    /// </summary>
    /// <param name="servicePlugins">The install's new plugins that <see cref="RegistersServices">register services</see>.</param>
    public IReadOnlyList<string> HotLoadBlockers(IEnumerable<PlannedPlugin> servicePlugins)
    {
        var reasons = new List<string>();
        lock (_gate)
        {
            foreach (var plugin in servicePlugins)
            {
                if (_cells.TryGetValue(plugin.Cell!, out var services) && services.IsCreated)
                {
                    reasons.Add($"{plugin.Id} registers services, but cell '{plugin.Cell}' already has its service provider, which can't take new registrations while it runs.");
                }
            }
        }

        return reasons;
    }

    /// <summary>
    /// Disposes a cell's provider (after its extensions and plugins), and before it the entry points of the cell's
    /// plugins that were prepared but never activated. Plugin code: runs on the caller's context. What was disposed is
    /// taken, so calling it again (or <see cref="DisposeAllAsync"/>) disposes only what was created since.
    /// </summary>
    public async ValueTask DisposeCellAsync(string cell)
    {
        List<CellServices> services;
        lock (_gate)
        {
            _cells.Remove(cell);
            services = _created.Where(s => string.Equals(s.Cell, cell, StringComparison.Ordinal)).ToList();
        }

        foreach (var cellServices in services)
        {
            await DisposeServicesAsync(cellServices).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Disposes every cell's provider left, in reverse creation order, including one set after its cell was disposed
    /// (an activation that disposal didn't wait for).
    /// </summary>
    public async ValueTask DisposeAllAsync()
    {
        List<CellServices> remaining;
        lock (_gate)
        {
            remaining = _created.ToList();
        }

        for (var i = remaining.Count - 1; i >= 0; i--)
        {
            await DisposeServicesAsync(remaining[i]).ConfigureAwait(true);
        }
    }

    private async ValueTask DisposeServicesAsync(CellServices services)
    {
        foreach (var (id, entryPoint) in services.TakeUnclaimedEntryPoints())
        {
            await DisposeQuietlyAsync(entryPoint, $"the entry point of plugin {id}", services.Cell).ConfigureAwait(true);
        }

        if (services.TakeProvider() is { } provider)
        {
            await DisposeQuietlyAsync(provider, "the service provider", services.Cell).ConfigureAwait(true);
        }
    }

    private async ValueTask DisposeQuietlyAsync(object instance, string what, string cell)
    {
        try
        {
            if (instance is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync().ConfigureAwait(true);
            }
            else if (instance is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        catch (Exception ex)
        {
            CellServicesLog.DisposeFailed(logger, ex, what, cell);
        }
    }

    /// <summary>One cell's provider (or why it couldn't be created) and its prepared plugins not activated yet.</summary>
    private sealed class CellServices(string cell)
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<string, PreparedPlugin> _prepared = new(StringComparer.OrdinalIgnoreCase);
        private IServiceProvider? _provider;
        private Exception? _fault;

        public string Cell { get; } = cell;

        /// <summary>Whether the factory was called (whatever the outcome).</summary>
        public bool IsCreated
        {
            get
            {
                lock (_gate)
                {
                    return _provider is not null || _fault is not null;
                }
            }
        }

        public IServiceProvider GetProvider()
        {
            lock (_gate)
            {
                if (_fault is { } fault)
                {
                    throw new InvalidOperationException($"Cell '{Cell}' couldn't create its service provider: {fault.Message}", fault);
                }

                return _provider ?? throw new ObjectDisposedException($"The service provider of cell '{Cell}'");
            }
        }

        public void SetProvider(IServiceProvider provider)
        {
            lock (_gate)
            {
                _provider = provider;
            }
        }

        public void SetFault(Exception fault)
        {
            lock (_gate)
            {
                _fault = fault;
            }
        }

        public IServiceProvider? TakeProvider()
        {
            lock (_gate)
            {
                var provider = _provider;
                _provider = null;
                return provider;
            }
        }

        public void AddPrepared(string id, PreparedPlugin prepared)
        {
            lock (_gate)
            {
                _prepared[id] = prepared;
            }
        }

        public PreparedPlugin? TakePrepared(string id)
        {
            lock (_gate)
            {
                return _prepared.Remove(id, out var prepared) ? prepared : null;
            }
        }

        public List<(string Id, IPlugin EntryPoint)> TakeUnclaimedEntryPoints()
        {
            lock (_gate)
            {
                var unclaimed = _prepared.Where(p => p.Value.EntryPoint is not null).Select(p => (p.Key, p.Value.EntryPoint!)).ToList();
                _prepared.Clear();
                return unclaimed;
            }
        }
    }
}

/// <summary>Cell service provider log messages. Event IDs 2300–2399.</summary>
internal static partial class CellServicesLog
{
    [LoggerMessage(EventId = 2300, Level = LogLevel.Debug, Message = "Created the service provider of cell '{Cell}' with the registrations of {Count} plugin(s)")]
    public static partial void Created(ILogger logger, string cell, int count);

    [LoggerMessage(EventId = 2301, Level = LogLevel.Error, Message = "Cell '{Cell}' couldn't create its service provider; its plugins fault")]
    public static partial void CreateFailed(ILogger logger, Exception exception, string cell);

    [LoggerMessage(EventId = 2302, Level = LogLevel.Warning, Message = "Disposing {What} of cell '{Cell}' threw")]
    public static partial void DisposeFailed(ILogger logger, Exception exception, string what, string cell);
}

using System.Collections;
using Microsoft.Extensions.DependencyInjection;

namespace Alcove.DependencyInjection.Composition;

/// <summary>
/// A cell's view of services: what the cell's plugins registered, falling back to the host's services.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item>A service the cell registered comes from the cell; anything else comes from the host.</item>
///   <item><c>IEnumerable&lt;T&gt;</c> is the host's services followed by the cell's (so the last one is the one a
///   single resolution returns when both register <c>T</c>).</item>
///   <item>Keyed services: from the cell when it registered the key, otherwise from the host.</item>
///   <item>It is its own <see cref="IServiceProvider"/>, <see cref="IServiceScopeFactory"/> and (when the host has
///   one) <see cref="IServiceProviderIsService"/>, so injected providers and scopes keep the fallback. A scope pairs a
///   cell scope with a host scope; disposing it disposes both.</item>
///   <item>Disposing the root view disposes the cell's container, never the host's.</item>
/// </list>
/// </remarks>
internal sealed class CellServiceProvider :
    IServiceProvider,
    IKeyedServiceProvider,
    IServiceProviderIsKeyedService,
    IServiceScopeFactory,
    IDisposable,
    IAsyncDisposable
{
    private readonly IServiceProvider _cell;
    private readonly IServiceProvider _host;
    private readonly IServiceProviderIsService? _cellIsService;
    private readonly IServiceProviderIsService? _hostIsService;
    private readonly object?[] _owned;
    private int _disposed;

    private CellServiceProvider(IServiceProvider cell, IServiceProvider host, object?[] owned)
    {
        _cell = cell;
        _host = host;
        _owned = owned;
        _cellIsService = cell.GetService<IServiceProviderIsService>();
        _hostIsService = host.GetService<IServiceProviderIsService>();
    }

    /// <summary>The root view over a cell's container, which it owns.</summary>
    public static CellServiceProvider CreateRoot(ServiceProvider cell, IServiceProvider host)
    {
        var root = new CellServiceProvider(cell, host, [cell]);
        Holder.Set(cell, root);
        return root;
    }

    public object? GetService(Type serviceType)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceType == typeof(IServiceProvider)
            || serviceType == typeof(IKeyedServiceProvider)
            || serviceType == typeof(IServiceScopeFactory)
            || ((serviceType == typeof(IServiceProviderIsService) || serviceType == typeof(IServiceProviderIsKeyedService)) && _hostIsService is not null))
        {
            return this;
        }

        if (serviceType.IsConstructedGenericType && serviceType.GetGenericTypeDefinition() == typeof(IEnumerable<>))
        {
            return Concat(serviceType.GenericTypeArguments[0], _host.GetService(serviceType), _cell.GetService(serviceType));
        }

        return InCell(serviceType) ? _cell.GetService(serviceType) : _host.GetService(serviceType);
    }

    public object? GetKeyedService(Type serviceType, object? serviceKey)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (_cellIsService is IServiceProviderIsKeyedService cellKeyed && cellKeyed.IsKeyedService(serviceType, serviceKey))
        {
            return ((IKeyedServiceProvider)_cell).GetKeyedService(serviceType, serviceKey);
        }

        return (_host as IKeyedServiceProvider)?.GetKeyedService(serviceType, serviceKey);
    }

    public object GetRequiredKeyedService(Type serviceType, object? serviceKey) =>
        GetKeyedService(serviceType, serviceKey)
        ?? throw new InvalidOperationException($"No keyed service for type '{serviceType}' (key '{serviceKey}') is registered in the cell or the host.");

    public bool IsService(Type serviceType)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceType == typeof(IServiceProvider)
            || serviceType == typeof(IKeyedServiceProvider)
            || serviceType == typeof(IServiceScopeFactory)
            || serviceType == typeof(IServiceProviderIsService)
            || serviceType == typeof(IServiceProviderIsKeyedService)
            || InCell(serviceType)
            || _hostIsService?.IsService(serviceType) == true;
    }

    public bool IsKeyedService(Type serviceType, object? serviceKey) =>
        (_cellIsService as IServiceProviderIsKeyedService)?.IsKeyedService(serviceType, serviceKey) == true
        || (_hostIsService as IServiceProviderIsKeyedService)?.IsKeyedService(serviceType, serviceKey) == true;

    public IServiceScope CreateScope()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var cellScope = _cell.GetRequiredService<IServiceScopeFactory>().CreateScope();
        var hostScope = _host.GetService<IServiceScopeFactory>()?.CreateScope();
        var provider = new CellServiceProvider(cellScope.ServiceProvider, hostScope?.ServiceProvider ?? _host, [cellScope, hostScope]);
        Holder.Set(cellScope.ServiceProvider, provider);
        return new Scope(provider);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var owned in _owned)
        {
            (owned as IDisposable)?.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // The cell's services first: they may depend on the host scope's. Plugin code: keep the caller's context.
        foreach (var owned in _owned)
        {
            if (owned is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync().ConfigureAwait(true);
            }
            else
            {
                (owned as IDisposable)?.Dispose();
            }
        }
    }

    /// <summary>Whether a plugin of the cell registered the type (the cell container's own built-ins don't count).</summary>
    private bool InCell(Type serviceType) => _cellIsService?.IsService(serviceType) == true && serviceType != typeof(Holder);

    private static Array Concat(Type element, object? first, object? second)
    {
        var items = new List<object>();
        foreach (var source in new[] { first, second })
        {
            if (source is IEnumerable enumerable)
            {
                items.AddRange(enumerable.Cast<object>());
            }
        }

        var array = Array.CreateInstance(element, items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            array.SetValue(items[i], i);
        }

        return array;
    }

    /// <summary>
    /// Registered (scoped) in every cell container: the cell view of the root or of the scope that resolves it, so
    /// the cell's factories resolve their dependencies with the host fallback.
    /// </summary>
    internal sealed class Holder
    {
        private IServiceProvider? _provider;

        public static IServiceProvider Of(IServiceProvider cell) =>
            cell.GetRequiredService<Holder>()._provider
            ?? throw new InvalidOperationException("This cell scope wasn't created through the cell's service provider.");

        public static void Set(IServiceProvider cell, IServiceProvider view) => cell.GetRequiredService<Holder>()._provider = view;
    }

    private sealed class Scope(CellServiceProvider provider) : IServiceScope, IAsyncDisposable
    {
        public IServiceProvider ServiceProvider => provider;

        public void Dispose() => provider.Dispose();

        public ValueTask DisposeAsync() => provider.DisposeAsync();
    }
}

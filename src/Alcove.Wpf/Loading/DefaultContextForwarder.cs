using System.IO;
using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using Alcove.Cells;
using Microsoft.Extensions.Logging;

namespace Alcove.Wpf.Loading;

/// <summary>A cell that owns a private copy of an assembly, as the forwarder sees it.</summary>
internal sealed record ForwardCandidate(string Cell, CellAssembly Assembly);

/// <summary>Where the forwarder sends a failed default-context load.</summary>
/// <param name="Cell">The one cell to ask, or <see langword="null"/> (none owns a copy, or several own different ones).</param>
/// <param name="Candidates">Every cell that owns a private copy of the name.</param>
internal sealed record ForwardDecision(string? Cell, IReadOnlyList<ForwardCandidate> Candidates)
{
    /// <summary>Several cells own different copies, so none is chosen.</summary>
    public bool IsAmbiguous => Cell is null && Candidates.Count > 1;
}

/// <summary>
/// The opt-in <c>AssemblyLoadContext.Default.Resolving</c> forwarder (spike: wpf-across-load-contexts, scenario 10).
/// WPF's fallback loads (<c>Assembly.Load</c> from PresentationFramework: BAML naming an assembly version no loaded
/// copy has, e.g. a dependency rolled forward in its cell, and external theme assemblies) bind in the default context,
/// which never asks a cell. When such a load fails, this forwards it to the single cell of the host that owns a private
/// copy with that simple name (the version is ignored; the cell still rejects a lower one).
/// </summary>
/// <remarks>
/// Process-wide by nature (the default context is), so it's the documented exception to "no global state" (ADR 0012):
/// registered per host and removed when the host is disposed. It only answers names its host's cells own, and never
/// when several cells own different copies (it logs instead): any choice would give the other cells' XAML a foreign copy.
/// Copies that are identical (same package ID and version) and WPF-relevant count as one, since the cells share them
/// (ADR 0015). Satellite assemblies aren't forwarded.
/// </remarks>
internal sealed class DefaultContextForwarder : IDisposable
{
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly List<CellInfo> _cells = [];
    private readonly ThreadLocal<HashSet<string>> _forwarding = new(() => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    private bool _registered;
    private bool _disposed;

    public DefaultContextForwarder(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>Whether the handler is registered on the default context.</summary>
    public bool IsRegistered
    {
        get
        {
            lock (_gate)
            {
                return _registered;
            }
        }
    }

    /// <summary>Registers the handler on <see cref="AssemblyLoadContext.Default"/>.</summary>
    public void Register()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_registered)
            {
                return;
            }

            AssemblyLoadContext.Default.Resolving += OnResolving;
            _registered = true;
        }

        WpfLog.Forwarder(_logger, "registered");
    }

    /// <summary>Adds a cell of the host; its private assemblies are read again at every failed load (they grow with hot installs).</summary>
    public void Add(CellInfo cell)
    {
        ArgumentNullException.ThrowIfNull(cell);
        lock (_gate)
        {
            _cells.Add(cell);
        }
    }

    /// <summary>Removes the handler. Loads it already answered stay answered (the default context keeps them).</summary>
    public void Dispose()
    {
        bool removed;
        lock (_gate)
        {
            removed = _registered;
            if (_registered)
            {
                AssemblyLoadContext.Default.Resolving -= OnResolving;
                _registered = false;
            }

            _disposed = true;
        }

        if (removed)
        {
            WpfLog.Forwarder(_logger, "removed");
        }
    }

    /// <summary>Chooses the cell for a name (pure: tested on every OS).</summary>
    public static ForwardDecision Decide(IEnumerable<ForwardCandidate> owners)
    {
        var candidates = owners.OrderBy(c => c.Cell, StringComparer.Ordinal).ToList();
        if (candidates.Count == 0)
        {
            return new ForwardDecision(null, candidates);
        }

        // Identical WPF-relevant copies are one instance shared by their cells (ADR 0015): any of them gives it.
        var first = candidates[0].Assembly;
        var allShared = candidates.All(c => c.Assembly.IsWpfRelevant
            && string.Equals(c.Assembly.PackageId, first.PackageId, StringComparison.OrdinalIgnoreCase)
            && c.Assembly.PackageVersion == first.PackageVersion);
        return candidates.Count == 1 || allShared
            ? new ForwardDecision(candidates[0].Cell, candidates)
            : new ForwardDecision(null, candidates);
    }

    /// <summary>The decision for a requested name over the host's cells as they are now.</summary>
    public ForwardDecision Decide(AssemblyName name)
    {
        ArgumentNullException.ThrowIfNull(name);
        List<CellInfo> cells;
        lock (_gate)
        {
            cells = [.. _cells];
        }

        return Decide(cells.SelectMany(cell => cell.PrivateAssemblies
            .Where(a => string.Equals(a.Name, name.Name, StringComparison.OrdinalIgnoreCase))
            .Select(a => new ForwardCandidate(cell.Name, a))));
    }

    /// <summary>The handler: forwards to the one owning cell, or returns <see langword="null"/>.</summary>
    internal Assembly? OnResolving(AssemblyLoadContext context, AssemblyName name)
    {
        if (string.IsNullOrEmpty(name.Name)
            || (!string.IsNullOrEmpty(name.CultureName) && !string.Equals(name.CultureName, CultureInfo.InvariantCulture.Name, StringComparison.Ordinal)))
        {
            return null;
        }

        var decision = Decide(name);
        if (decision.IsAmbiguous)
        {
            WpfLog.ForwardAmbiguous(_logger, name.FullName,
                string.Join("; ", decision.Candidates.Select(c => $"{c.Assembly.PackageId} {c.Assembly.PackageVersion} in cell '{c.Cell}'")));
            return null;
        }

        if (decision.Cell is not { } cellName)
        {
            return null;
        }

        CellInfo cell;
        lock (_gate)
        {
            cell = _cells.First(c => string.Equals(c.Name, cellName, StringComparison.Ordinal));
        }

        // A cell that answers the name from the host would come back here: never forward a name twice on one thread.
        var forwarding = _forwarding.Value!;
        if (!forwarding.Add(name.Name))
        {
            return null;
        }

        try
        {
            var assembly = cell.LoadContext.LoadFromAssemblyName(name);
            WpfLog.Forwarded(_logger, name.FullName, cell.Name, assembly.GetName().Version);
            return assembly;
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or UnauthorizedAccessException)
        {
            WpfLog.ForwardFailed(_logger, name.FullName, cell.Name, ex.Message);
            return null;
        }
        finally
        {
            forwarding.Remove(name.Name);
        }
    }
}

using System.Reflection;

namespace Alcove.Cells;

/// <summary>
/// Loads identical WPF-relevant private assemblies (same package ID, version and assembly name) once and
/// hands the same instance to every cell that needs them (ADR 0015). WPF finds assemblies by name across the
/// whole process, so two identical copies in different cells would break the earlier one's views.
/// </summary>
internal sealed class SharedAssemblyRegistry
{
    // The registry lock only guards the dictionary; the load runs in the entry's Lazy, so a load never holds a
    // lock other keys need. Concurrent requests for one key wait for that one load and share its result.
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Lazy<(Assembly Assembly, string Cell)>> _loaded = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the instance already loaded for this package version and assembly, or loads it with
    /// <paramref name="load"/> (into the requesting cell) and remembers it.
    /// </summary>
    /// <returns>The assembly, and the cell that loaded it.</returns>
    public (Assembly Assembly, string OwnerCell) GetOrLoad(PrivateAssembly asset, string requestingCell, Func<Assembly> load)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(load);
        var key = Key(asset);
        Lazy<(Assembly, string)> entry;
        lock (_gate)
        {
            if (!_loaded.TryGetValue(key, out entry!))
            {
                entry = new Lazy<(Assembly, string)>(() => (load(), requestingCell), LazyThreadSafetyMode.ExecutionAndPublication);
                _loaded[key] = entry;
            }
        }

        try
        {
            return entry.Value;
        }
        catch
        {
            // A failed load isn't cached: the next request tries again.
            lock (_gate)
            {
                if (_loaded.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
                {
                    _loaded.Remove(key);
                }
            }

            throw;
        }
    }

    /// <summary>The shared instance for this package version and assembly, if one is loaded.</summary>
    public Assembly? Find(PrivateAssembly asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        lock (_gate)
        {
            return _loaded.TryGetValue(Key(asset), out var entry) && entry.IsValueCreated ? entry.Value.Assembly : null;
        }
    }

    private static string Key(PrivateAssembly asset) =>
        $"{asset.PackageId}/{asset.PackageVersion.ToNormalizedString()}/{asset.Name}";
}

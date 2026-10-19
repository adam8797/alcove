using System.IO;
using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Alcove.Wpf.Internal;

/// <summary>
/// Loads a cell's WPF-relevant private assemblies as soon as the cell exists (spike: wpf-across-load-contexts,
/// scenario 9). WPF only finds assemblies that are already loaded somewhere in the process; when it doesn't, it falls
/// back to <c>Assembly.Load</c> in the default context, which never sees a cell's private assemblies. So a control
/// library a plugin uses only from XAML must be loaded before the XAML is.
/// </summary>
internal static class WpfPreloader
{
    /// <summary>The assemblies to pre-load: the WPF-relevant ones, by name.</summary>
    public static IReadOnlyList<CellAssembly> Select(IEnumerable<CellAssembly> assemblies) =>
        [.. assemblies.Where(a => a.IsWpfRelevant).OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// Loads the WPF-relevant ones of <paramref name="assemblies"/> through the cell (so its answers apply, including
    /// sharing identical copies across cells). A failure is logged and doesn't stop the others.
    /// </summary>
    /// <returns>The assemblies loaded.</returns>
    public static IReadOnlyList<Assembly> Preload(CellInfo cell, IEnumerable<CellAssembly> assemblies, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(cell);
        var loaded = new List<Assembly>();
        foreach (var asset in Select(assemblies))
        {
            try
            {
                loaded.Add(cell.LoadContext.LoadFromAssemblyName(new AssemblyName(asset.Name)));
            }
            catch (Exception ex) when (ex is IOException or BadImageFormatException or UnauthorizedAccessException)
            {
                WpfLog.PreloadFailed(logger, asset.Name, cell.Name, ex.Message);
            }
        }

        if (loaded.Count > 0)
        {
            WpfLog.Preloaded(logger, loaded.Count, cell.Name, string.Join(", ", loaded.Select(a => a.GetName().Name)));
        }

        return loaded;
    }
}

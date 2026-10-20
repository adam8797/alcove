using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Logging;
using NuGet.Versioning;

namespace Alcove.Cells;

/// <summary>
/// Watches the cells of one plugin host: the extension point UI add-ons (such as <c>Alcove.Wpf</c>) plug into
/// (<see cref="PluginHostOptions.CellObservers"/>). The core knows no UI framework; it only tells observers when a cell
/// is created or grows, and when a plugin's assemblies are loaded.
/// </summary>
/// <remarks>
/// <para>
/// Each host creates its own observers (one per factory in <see cref="PluginHostOptions.CellObservers"/>), so an
/// observer only ever sees one host's cells. When the host is disposed, after its plugins and cell service providers,
/// it disposes every observer that implements <see cref="IAsyncDisposable"/> or <see cref="IDisposable"/>, on the
/// thread that disposes the host.
/// </para>
/// <para>
/// Calls are made on the thread that loads or installs, never under the library's locks, so an observer may load
/// assemblies into the cell. An exception from <see cref="CellCreated"/> or <see cref="CellExtended"/> is logged and
/// doesn't stop the load; one from <see cref="PluginLoading"/> faults that plugin.
/// </para>
/// </remarks>
public interface ICellObserver
{
    /// <summary>A cell was created; nothing has loaded into it yet.</summary>
    /// <param name="cell">The new cell.</param>
    void CellCreated(CellInfo cell) { }

    /// <summary>
    /// A hot install added plugins to a cell that already exists, before their assemblies load. Not called for the
    /// cells an install creates (those get <see cref="CellCreated"/>).
    /// </summary>
    /// <param name="cell">The cell, whose <see cref="CellInfo.PrivateAssemblies"/> now include the additions.</param>
    /// <param name="added">The private assemblies the install added to the cell's plan.</param>
    void CellExtended(CellInfo cell, IReadOnlyList<CellAssembly> added) { }

    /// <summary>
    /// A plugin's own assemblies are loaded into its cell and its entry point is created; next it registers its
    /// extensions. Runs inside the cell's contextual reflection scope.
    /// </summary>
    /// <param name="cell">The plugin's cell.</param>
    /// <param name="plugin">The plugin.</param>
    /// <param name="assemblies">The plugin's own assemblies, loaded into <paramref name="cell"/>.</param>
    void PluginLoading(CellInfo cell, PluginInfo plugin, IReadOnlyList<Assembly> assemblies) { }
}

/// <summary>A cell as an <see cref="ICellObserver"/> sees it: its name, its load context and its planned private assemblies.</summary>
public sealed class CellInfo
{
    private readonly Cell _cell;

    internal CellInfo(Cell cell)
    {
        _cell = cell;
    }

    /// <summary>The cell's name (as in <see cref="PluginInfo.Cell"/>).</summary>
    public string Name => _cell.Name;

    /// <summary>The cell's <see cref="AssemblyLoadContext"/>. Load through it (e.g. <see cref="AssemblyLoadContext.LoadFromAssemblyName"/>) so the cell's answers apply.</summary>
    public AssemblyLoadContext LoadContext => _cell;

    /// <summary>The IDs of the plugins planned in the cell, sorted.</summary>
    public IReadOnlyList<string> Plugins => [.. _cell.Plugins.Order(StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// The cell-private assemblies in the cell's current plan (its plugins' own assemblies included), sorted by name.
    /// A hot install into the cell adds to them. Planned doesn't mean loaded: assemblies load when first needed.
    /// </summary>
    public IReadOnlyList<CellAssembly> PrivateAssemblies => _cell.GetPrivateAssemblies();

    /// <inheritdoc />
    public override string ToString() => $"Cell '{Name}'";
}

/// <summary>A cell-private assembly in a cell's plan: a file in the plugin root.</summary>
/// <param name="Name">The assembly's simple name.</param>
/// <param name="Path">The file.</param>
/// <param name="PackageId">The package the file belongs to.</param>
/// <param name="PackageVersion">That package's version.</param>
/// <param name="IsWpfRelevant">
/// Whether it's a WPF-relevant assembly: it contains compiled XAML, or is referenced by one that does. WPF looks these
/// up by name across the whole process, so they must be loaded before XAML asks for them.
/// </param>
public sealed record CellAssembly(string Name, string Path, string PackageId, NuGetVersion PackageVersion, bool IsWpfRelevant);

/// <summary>Creates a host's <see cref="ICellObserver"/>.</summary>
/// <param name="loggerFactory">The host's logger factory (<see cref="PluginHostOptions.LoggerFactory"/>, or a null one).</param>
/// <returns>The observer, used for this host only.</returns>
public delegate ICellObserver CellObserverFactory(ILoggerFactory loggerFactory);

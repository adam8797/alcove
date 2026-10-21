using Alcove.Cells;
using Alcove.Packaging;
using Alcove.Storage;
using Alcove.Validation;

namespace Alcove.Loading;

/// <summary>One installed (or development) plugin as validation left it.</summary>
/// <param name="State">Its state file entry.</param>
/// <param name="Manifest">Its manifest, if it could be read.</param>
/// <param name="Cell">Its cell, if it's enabled.</param>
/// <param name="Status">Disabled, NotLoaded (blocked by validation), or NotLoaded with no reason (to be loaded).</param>
/// <param name="Reason">Why it won't load, or <see langword="null"/> if it's loadable.</param>
internal sealed record PlannedPlugin(PluginState State, PluginManifest? Manifest, string? Cell, PluginStatus Status, string? Reason)
{
    public string Id => State.Id;

    /// <summary>
    /// The entry point found in its assemblies when the manifest names none: its one public, non-abstract class
    /// implementing <see cref="IPlugin"/>.
    /// </summary>
    public string? DiscoveredEntryPoint { get; init; }

    /// <summary>The entry point's full type name: the manifest's, else the discovered one; <see langword="null"/> for none.</summary>
    public string? EntryPoint => Manifest?.EntryPoint ?? DiscoveredEntryPoint;

    /// <summary>Whether it's a development plugin, loaded from its build output and never recorded in the state file.</summary>
    public bool IsDevelopment { get; init; }

    /// <summary>Whether validation lets it load.</summary>
    public bool IsLoadable => Status == PluginStatus.NotLoaded && Reason is null;

    /// <summary>IDs of the plugins it depends on directly.</summary>
    public IEnumerable<string> PluginDependencies =>
        State.Dependencies.Where(d => d.Classification == DependencyClass.Plugin).Select(d => d.Id);
}

/// <summary>
/// What validation decided: the report, every installed plugin's fate, the cells to create (for the loadable
/// plugins only) and the order to load them in.
/// </summary>
internal sealed record LoadPlan(
    ValidationReport Report,
    IReadOnlyList<PlannedPlugin> Plugins,
    IReadOnlyList<CellClassification> Cells,
    IReadOnlyList<PlannedPlugin> LoadOrder);

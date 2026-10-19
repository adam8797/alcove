using NuGet.Versioning;

namespace Alcove.Internal.State;

// The JSON source generator sets every init-only property at construction, so a property missing from the
// file gets default(T) instead of its initializer. Hence: collections use "init => field = value ?? []" (a
// missing list is an empty one), and scalars with a non-default initializer use "set".

/// <summary>
/// The state file, <c>&lt;root&gt;/state.json</c>: what is installed, pinned exactly. Only the library writes it,
/// atomically and while holding the root lock (architecture §2).
/// </summary>
internal sealed record StateDocument
{
    /// <summary>The state file schema version this version of Alcove writes.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>The state file schema version.</summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>Every installed plugin, enabled or not.</summary>
    public IReadOnlyList<PluginState> Plugins { get; init => field = value ?? []; } = [];

    /// <summary>An empty state (nothing installed).</summary>
    public static StateDocument Empty { get; } = new();

    /// <summary>The plugin with this ID (case-insensitive), or <see langword="null"/>.</summary>
    public PluginState? Find(string pluginId) =>
        Plugins.FirstOrDefault(p => string.Equals(p.Id, pluginId, StringComparison.OrdinalIgnoreCase));
}

/// <summary>One installed plugin.</summary>
internal sealed record PluginState
{
    /// <summary>The plugin's package ID.</summary>
    public required string Id { get; init; }

    /// <summary>The active version.</summary>
    public required NuGetVersion Version { get; init; }

    /// <summary>
    /// Previous versions still on disk for rollback, newest first: each the full entry it had while it was active
    /// (its own <see cref="PreviousVersions"/>, <see cref="StagedChange"/> and <see cref="StagedEnabled"/> empty).
    /// </summary>
    public IReadOnlyList<PluginState> PreviousVersions { get; init => field = value ?? []; } = [];

    /// <summary>Whether the plugin is loaded at startup.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>A version switch or uninstall applied at the next startup, if any.</summary>
    public StagedChange? StagedChange { get; init; }

    /// <summary>The enabled flag from the next startup on, if enabling or disabling was staged.</summary>
    public bool? StagedEnabled { get; init; }

    /// <summary>The package's NuGet <c>contentHash</c> (base64 SHA-512 without the signature).</summary>
    public required string ContentHash { get; init; }

    /// <summary>
    /// The feed (source URL or folder) the package was downloaded from, checked against
    /// <see cref="TrustPolicy.AllowedFeeds"/> when a later plan reuses it. <see langword="null"/> in state files
    /// written before it was recorded.
    /// </summary>
    public string? Source { get; init; }

    /// <summary>Every extracted file of the plugin package, for the cheap startup check.</summary>
    public IReadOnlyList<FileRecord> Files { get; init => field = value ?? []; } = [];

    /// <summary>The plugin package's own runtime assemblies.</summary>
    public IReadOnlyList<AssemblyRecord> Assemblies { get; init => field = value ?? []; } = [];

    /// <summary>The resolved dependency set: every package the plugin needs, with its class.</summary>
    public IReadOnlyList<ResolvedDependency> Dependencies { get; init => field = value ?? []; } = [];

    /// <summary>
    /// Simple names of the assemblies whose types appear in the plugin's public API (its exposed dependencies),
    /// found by a metadata scan at install. Cells using this plugin get them through its cell.
    /// </summary>
    public IReadOnlyList<string> ExposedDependencies { get; init => field = value ?? []; } = [];

    /// <summary>The cell the plugin was assigned to at install.</summary>
    public string? Cell { get; init; }

    /// <summary>The schema version of the plugin's manifest.</summary>
    public int ManifestSchemaVersion { get; set; } = PluginManifest.CurrentSchemaVersion;
}

/// <summary>A package in a plugin's resolved dependency set.</summary>
internal sealed record ResolvedDependency
{
    /// <summary>The package ID.</summary>
    public required string Id { get; init; }

    /// <summary>The exact resolved version.</summary>
    public required NuGetVersion Version { get; init; }

    /// <summary>Where the package's assemblies come from.</summary>
    public required DependencyClass Classification { get; init; }

    /// <summary>The package's <c>contentHash</c>, when it was downloaded into the plugin root.</summary>
    public string? ContentHash { get; init; }

    /// <summary>
    /// The feed the package was downloaded from, when it is in the plugin root (see <see cref="PluginState.Source"/>).
    /// </summary>
    public string? Source { get; init; }

    /// <summary>Every extracted file, when the package is in the plugin root.</summary>
    public IReadOnlyList<FileRecord> Files { get; init => field = value ?? []; } = [];

    /// <summary>The package's runtime assemblies, when it is in the plugin root.</summary>
    public IReadOnlyList<AssemblyRecord> Assemblies { get; init => field = value ?? []; } = [];

    /// <summary>
    /// The version ranges declared on this package (in the <c>.nuspec</c> group for the host's framework) by the
    /// plugin and by its cell-private dependencies. For host-owned packages (e.g. the contracts) they are the
    /// compatibility gate, checked against the running host at every startup.
    /// </summary>
    public IReadOnlyList<DependencyRequest> RequestedBy { get; init => field = value ?? []; } = [];
}

/// <summary>A dependency range one package declares on another.</summary>
/// <param name="PackageId">The package declaring the dependency (the plugin, or one of its cell-private dependencies).</param>
/// <param name="Range">The declared version range, e.g. <c>[2.0.0, 3.0.0)</c>.</param>
internal sealed record DependencyRequest(string PackageId, VersionRange Range);

/// <summary>The class of a resolved dependency (architecture §4).</summary>
internal enum DependencyClass
{
    /// <summary>Provided by the host and always taken from it; a conflict makes the plugin incompatible.</summary>
    HostOwned,

    /// <summary>Provided by the host, whose version satisfies the plugin.</summary>
    HostShared,

    /// <summary>Loaded inside the plugin's cell from the plugin root.</summary>
    CellPrivate,

    /// <summary>Another plugin (a plugin dependency), loaded in its own cell.</summary>
    Plugin,
}

/// <summary>An extracted file: path relative to the package folder (forward slashes), size and timestamp.</summary>
internal sealed record FileRecord(string Path, long Size, DateTime LastWriteTimeUtc);

/// <summary>A runtime assembly in a package.</summary>
internal sealed record AssemblyRecord
{
    /// <summary>The assembly's simple name.</summary>
    public required string Name { get; init; }

    /// <summary>The path relative to the package folder (forward slashes), e.g. <c>lib/net8.0/Foo.dll</c>.</summary>
    public required string Path { get; init; }

    /// <summary>The <c>AssemblyVersion</c>, if known.</summary>
    public string? AssemblyVersion { get; init; }

    /// <summary>
    /// Whether the assembly is WPF-relevant: it contains <c>&lt;Name&gt;.g.resources</c> or is referenced by one
    /// that does. Identical WPF-relevant private assemblies are loaded once and shared across cells.
    /// </summary>
    public bool WpfRelevant { get; init; }
}

/// <summary>
/// A version switch or uninstall recorded now and applied at the next startup, before anything loads. Enabling and
/// disabling are staged separately (<see cref="PluginState.StagedEnabled"/>), so they combine with a switch.
/// </summary>
internal sealed record StagedChange
{
    /// <summary>What happens at the next startup.</summary>
    public required StagedChangeKind Kind { get; init; }

    /// <summary>For an update or rollback: the version that becomes active.</summary>
    public NuGetVersion? TargetVersion { get; init; }

    /// <summary>
    /// For an update or rollback: the entry that becomes active (its packages already in the plugin root). An update
    /// may keep the version and only change the dependency set, when its cell was resolved again for another plugin.
    /// </summary>
    public PluginState? Target { get; init; }

    /// <summary>For an uninstall: whether plugins depending on this one are uninstalled too.</summary>
    public bool Cascade { get; init; }

    /// <summary>Why, when the app didn't ask for this plugin directly (e.g. "uninstalled with X").</summary>
    public string? Reason { get; init; }

    /// <summary>When the change was staged.</summary>
    public DateTimeOffset StagedAt { get; init; }
}

/// <summary>The kind of a <see cref="StagedChange"/>.</summary>
internal enum StagedChangeKind
{
    /// <summary>Switch to <see cref="StagedChange.Target"/> (already downloaded next to the current one).</summary>
    Update,

    /// <summary>Remove the plugin.</summary>
    Uninstall,

    /// <summary>Switch back to a previous version kept on disk.</summary>
    Rollback,
}

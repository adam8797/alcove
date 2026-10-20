using NuGet.Client;
using NuGet.ContentModel;
using NuGet.Frameworks;
using NuGet.RuntimeModel;

namespace Alcove.Packaging;

/// <summary>A satellite (localised resource) assembly in a package.</summary>
/// <param name="Path">Path relative to the package folder, e.g. <c>lib/net8.0/de/Foo.resources.dll</c>.</param>
/// <param name="Culture">The culture name, e.g. <c>de</c>.</param>
internal sealed record SatelliteAsset(string Path, string Culture);

/// <summary>The assets of one package for one framework and runtime identifier. Paths are relative to the package folder, with forward slashes.</summary>
internal sealed record PackageAssets(
    IReadOnlyList<string> RuntimeAssemblies,
    IReadOnlyList<string> NativeLibraries,
    IReadOnlyList<SatelliteAsset> ResourceAssemblies)
{
    /// <summary>No assets.</summary>
    public static PackageAssets None { get; } = new([], [], []);
}

/// <summary>
/// Selects a package's <c>lib/</c> and <c>runtimes/</c> assets for a framework and runtime identifier with
/// NuGet's own rules (<see cref="ManagedCodeConventions"/> + <see cref="ContentItemCollection.FindBestItemGroup"/>),
/// the same machinery restore uses, and the SDK's RID graph (embedded). <c>content</c>, <c>contentFiles</c>,
/// <c>build</c> and <c>tools</c> are ignored.
/// </summary>
internal sealed class AssetSelector
{
    private const string RidGraphResource = "Alcove.PortableRuntimeIdentifierGraph.json";

    private static readonly Lazy<AssetSelector> s_default = new(() => new AssetSelector(LoadEmbeddedRuntimeGraph()));

    private readonly ManagedCodeConventions _conventions;

    // ManagedCodeConventions caches parsed values in plain dictionaries, so concurrent selections (e.g. two hosts
    // validating at once through the shared Default) corrupt them. Selection is cheap; it's serialised.
    private readonly Lock _gate = new();

    /// <summary>Creates a selector for a RID graph.</summary>
    public AssetSelector(RuntimeGraph runtimeGraph)
    {
        ArgumentNullException.ThrowIfNull(runtimeGraph);
        RuntimeGraph = runtimeGraph;
        _conventions = new ManagedCodeConventions(runtimeGraph);
    }

    /// <summary>A selector using the embedded copy of the SDK's <c>PortableRuntimeIdentifierGraph.json</c>.</summary>
    public static AssetSelector Default => s_default.Value;

    /// <summary>The RID graph used for runtime identifier fallback.</summary>
    public RuntimeGraph RuntimeGraph { get; }

    /// <summary>Selects the assets of an extracted package folder.</summary>
    public PackageAssets SelectFromDirectory(string packageDirectory, NuGetFramework framework, string? runtimeIdentifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageDirectory);
        if (!Directory.Exists(packageDirectory))
        {
            return PackageAssets.None;
        }

        var files = Directory.EnumerateFiles(packageDirectory, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(packageDirectory, f).Replace('\\', '/'));
        return Select(files, framework, runtimeIdentifier);
    }

    /// <summary>
    /// Selects assets from a package's file list (paths relative to the package root, forward slashes).
    /// Without a runtime identifier, only RID-independent assets are selected.
    /// </summary>
    public PackageAssets Select(IEnumerable<string> packageFiles, NuGetFramework framework, string? runtimeIdentifier)
    {
        ArgumentNullException.ThrowIfNull(packageFiles);
        ArgumentNullException.ThrowIfNull(framework);

        var relevant = packageFiles
            .Select(f => f.Replace('\\', '/'))
            .Where(f => f.StartsWith("lib/", StringComparison.OrdinalIgnoreCase) || f.StartsWith("runtimes/", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (relevant.Count == 0)
        {
            return PackageAssets.None;
        }

        lock (_gate)
        {
            return SelectLocked(relevant, framework, runtimeIdentifier);
        }
    }

    private PackageAssets SelectLocked(List<string> relevant, NuGetFramework framework, string? runtimeIdentifier)
    {
        var items = new ContentItemCollection();
        items.Load(relevant);

        // With a RID: (tfm, rid), (tfm, each RID it imports...), then (tfm, no RID). Without: the tfm only.
        var criteria = string.IsNullOrEmpty(runtimeIdentifier)
            ? _conventions.Criteria.ForFramework(framework)
            : _conventions.Criteria.ForFrameworkAndRuntime(framework, runtimeIdentifier);

        var runtime = items.FindBestItemGroup(criteria, _conventions.Patterns.RuntimeAssemblies);
        var native = items.FindBestItemGroup(criteria, _conventions.Patterns.NativeLibraries);
        var resources = items.FindBestItemGroup(criteria, _conventions.Patterns.ResourceAssemblies);

        return new PackageAssets(
            runtime?.Items.Where(IsReal).Select(i => i.Path).ToList() ?? [],
            native?.Items.Where(IsReal).Select(i => i.Path).ToList() ?? [],
            resources?.Items.Where(IsReal)
                .Select(i => new SatelliteAsset(i.Path, i.Properties.TryGetValue("locale", out var locale) ? (string)locale : ""))
                .ToList() ?? []);
    }

    /// <summary>Reads the embedded RID graph.</summary>
    public static RuntimeGraph LoadEmbeddedRuntimeGraph()
    {
        using var stream = typeof(AssetSelector).Assembly.GetManifestResourceStream(RidGraphResource)
            ?? throw new InvalidOperationException($"The embedded resource '{RidGraphResource}' is missing.");
        return JsonRuntimeFormat.ReadRuntimeGraph(stream);
    }

    // "_._" marks an empty folder: "nothing needed for this framework".
    private static bool IsReal(ContentItem item) => !item.Path.EndsWith("/_._", StringComparison.Ordinal);
}

using Alcove.Packaging;
using NuGet.Packaging;
using NuGet.Versioning;

namespace Alcove.Storage;

/// <summary>
/// The package layout of a plugin root: <c>&lt;root&gt;/packages/&lt;id-lowercase&gt;/&lt;version&gt;/</c>,
/// exactly like NuGet's global packages folder.
/// </summary>
internal sealed class PackageLayout
{
    private readonly VersionFolderPathResolver _resolver;

    /// <summary>Creates the layout for a plugin root.</summary>
    public PackageLayout(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
        PackagesDirectory = Path.Combine(Root, "packages");
        _resolver = new VersionFolderPathResolver(PackagesDirectory, isLowercase: true);
    }

    /// <summary>The plugin root.</summary>
    public string Root { get; }

    /// <summary><c>&lt;root&gt;/packages</c>.</summary>
    public string PackagesDirectory { get; }

    /// <summary>The folder of an extracted package.</summary>
    /// <exception cref="ArgumentException"><paramref name="id"/> isn't a valid package ID.</exception>
    public string GetPackageDirectory(string id, NuGetVersion version) => _resolver.GetInstallPath(EnsureValid(id), version);

    /// <summary>The kept <c>.nupkg</c> of an extracted package.</summary>
    /// <exception cref="ArgumentException"><paramref name="id"/> isn't a valid package ID.</exception>
    public string GetPackageFilePath(string id, NuGetVersion version) => _resolver.GetPackageFilePath(EnsureValid(id), version);

    /// <summary>The <c>.nupkg.metadata</c> of an extracted package (its <c>contentHash</c> and source feed).</summary>
    /// <exception cref="ArgumentException"><paramref name="id"/> isn't a valid package ID.</exception>
    public string GetNupkgMetadataPath(string id, NuGetVersion version) => _resolver.GetNupkgMetadataPath(EnsureValid(id), version);

    /// <summary>The path of a plugin's manifest in its package folder.</summary>
    public string GetManifestPath(string id, NuGetVersion version) =>
        Path.Combine(GetPackageDirectory(id, version), PluginManifest.GetFileName(id));

    /// <summary>The plugin's private data directory, <c>&lt;root&gt;/data/&lt;plugin-id&gt;</c>.</summary>
    /// <exception cref="ArgumentException"><paramref name="pluginId"/> isn't a valid package ID.</exception>
    public string GetDataDirectory(string pluginId) => Path.Combine(Root, "data", EnsureValid(pluginId));

    /// <summary>Whether NuGet accepts the ID (so it's safe in a path: no separators, no <c>..</c>).</summary>
    public static bool IsValidPackageId(string? id) => id is not null && PackageIdValidator.IsValidPackageId(id);

    private static string EnsureValid(string id) =>
        IsValidPackageId(id) ? id : throw new ArgumentException($"'{id}' isn't a valid package ID.", nameof(id));
}

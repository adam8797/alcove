using System.Text.Json;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;

namespace Alcove.Internal.Feeds;

/// <summary>What resolution needs from a package: its dependency groups, package types and plugin manifest.</summary>
/// <param name="Identity">The package ID and version.</param>
/// <param name="DependencyGroups">The <c>.nuspec</c>'s dependency groups.</param>
/// <param name="PackageTypes">The <c>.nuspec</c>'s package types.</param>
/// <param name="Manifest">The plugin manifest, if the package has a valid one.</param>
/// <param name="ManifestError">Why the package's manifest can't be read, if it has an invalid one.</param>
/// <param name="Source">Where it was read from (a feed, or the plugin root).</param>
internal sealed record PackageMetadata(
    PackageIdentity Identity,
    IReadOnlyList<PackageDependencyGroup> DependencyGroups,
    IReadOnlyList<PackageType> PackageTypes,
    PluginManifest? Manifest,
    string? ManifestError,
    string Source)
{
    /// <summary>Whether the package is a plugin package: it has a plugin manifest (architecture §3).</summary>
    public bool IsPlugin => Manifest is not null || ManifestError is not null;

    /// <summary>Whether the package has a package type, e.g. the app's plugin package type.</summary>
    public bool HasPackageType(string name) =>
        PackageTypes.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The dependency group for a framework, or <see langword="null"/> if the package has groups but none fits.</summary>
    public PackageDependencyGroup? GroupFor(NuGetFramework framework) =>
        NuGetFrameworkUtility.GetNearest(DependencyGroups, framework, g => g.TargetFramework);

    /// <summary>The dependencies the package declares for a framework (the nearest group).</summary>
    public IReadOnlyList<PackageDependency> DependenciesFor(NuGetFramework framework) =>
        GroupFor(framework)?.Packages.ToList() ?? [];

    /// <summary>Reads a <c>.nupkg</c>.</summary>
    public static PackageMetadata Read(PackageArchiveReader reader, string source)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var identity = reader.GetIdentity();
        var manifestName = PluginManifest.GetFileName(identity.Id);
        var manifestFile = reader.GetFiles().FirstOrDefault(f => string.Equals(f, manifestName, StringComparison.OrdinalIgnoreCase));
        PluginManifest? manifest = null;
        string? error = null;
        if (manifestFile is not null)
        {
            try
            {
                using var stream = reader.GetStream(manifestFile);
                manifest = PluginManifest.Read(stream);
            }
            catch (JsonException ex)
            {
                error = ex.Message;
            }
        }

        return new PackageMetadata(identity, reader.GetPackageDependencies().ToList(), reader.GetPackageTypes().ToList(), manifest, error, source);
    }

    /// <summary>Reads an extracted package folder (its <c>.nuspec</c> and manifest).</summary>
    public static PackageMetadata ReadExtracted(string nuspecPath, string manifestPath, string source)
    {
        var nuspec = new NuspecReader(nuspecPath);
        PluginManifest? manifest = null;
        string? error = null;
        if (File.Exists(manifestPath))
        {
            try
            {
                using var stream = File.OpenRead(manifestPath);
                manifest = PluginManifest.Read(stream);
            }
            catch (JsonException ex)
            {
                error = ex.Message;
            }
        }

        return new PackageMetadata(nuspec.GetIdentity(), nuspec.GetDependencyGroups().ToList(), nuspec.GetPackageTypes().ToList(), manifest, error, source);
    }
}

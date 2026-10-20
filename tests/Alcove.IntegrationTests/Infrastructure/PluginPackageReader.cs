using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Alcove.Packaging;
using NuGet.Packaging;
using NuGet.Packaging.Core;

namespace Alcove.IntegrationTests.Infrastructure;

/// <summary>Reads what the tests check in a packed (plugin) package.</summary>
internal sealed class PluginPackageReader : IDisposable
{
    private readonly PackageArchiveReader _reader;

    public PluginPackageReader(string path)
    {
        Path = path;
        _reader = new PackageArchiveReader(path);
        Identity = _reader.GetIdentity();
    }

    public string Path { get; }

    public PackageIdentity Identity { get; }

    public NuspecReader Nuspec => _reader.NuspecReader;

    /// <summary>Package type names from the nuspec.</summary>
    public IReadOnlyList<string> PackageTypes => _reader.GetPackageTypes().Select(t => t.Name).ToList();

    /// <summary>Every file in the package (forward slashes), without the nuspec and NuGet's own metadata files.</summary>
    public IReadOnlyList<string> Files => _reader.GetFiles()
        .Where(f => !f.StartsWith("package/", StringComparison.Ordinal) && !f.StartsWith("_rels/", StringComparison.Ordinal) && f != "[Content_Types].xml"
            && !(f.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase) && !f.Contains('/', StringComparison.Ordinal)))
        .ToList();

    /// <summary>Dependencies of every target framework group.</summary>
    public IReadOnlyList<PackageDependency> Dependencies =>
        Nuspec.GetDependencyGroups().SelectMany(g => g.Packages).ToList();

    /// <summary>The dependency on <paramref name="id"/>, or <see langword="null"/>.</summary>
    public PackageDependency? Dependency(string id) =>
        Dependencies.SingleOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The plugin manifest at the package root, or <see langword="null"/>.</summary>
    public PluginManifest? ReadManifest()
    {
        var name = PluginManifest.GetFileName(Identity.Id);
        if (!Files.Contains(name))
        {
            return null;
        }

        using var stream = _reader.GetStream(name);
        return PluginManifest.Read(stream);
    }

    /// <summary>Reads an assembly's identity from its metadata, without loading it.</summary>
    public AssemblyName ReadAssemblyName(string packagePath)
    {
        using var stream = _reader.GetStream(packagePath);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        buffer.Position = 0;
        using var pe = new PEReader(buffer);
        return pe.GetMetadataReader().GetAssemblyDefinition().GetAssemblyName();
    }

    public void Dispose() => _reader.Dispose();
}

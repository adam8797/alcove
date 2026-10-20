using Alcove.Hosting;
using Alcove.Install;
using Alcove.Storage;
using NuGet.Packaging;
using NuGet.Versioning;

namespace Alcove.IntegrationTests.Infrastructure;

/// <summary>
/// Populates a temporary plugin root without a feed: copies built assemblies (or extracts fixture packages)
/// into <c>packages/&lt;id&gt;/&lt;version&gt;/</c> and writes the state file.
/// </summary>
internal sealed class PluginRootBuilder : IDisposable
{
    private readonly TempDirectory _root = new("alcove-root");

    public PluginRootBuilder()
    {
        Layout = new PackageLayout(_root.Path);
    }

    public string Root => _root.Path;

    public PackageLayout Layout { get; }

    /// <summary>Copies files into a package folder; <paramref name="files"/> maps package-relative paths to source files.</summary>
    public string AddPackage(string id, string version, params (string PackagePath, string Source)[] files)
    {
        var directory = Layout.GetPackageDirectory(id, NuGetVersion.Parse(version));
        foreach (var (packagePath, source) in files)
        {
            var target = Path.Combine(directory, packagePath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: true);
        }

        return directory;
    }

    /// <summary>A package holding one assembly at <c>lib/net10.0/</c>.</summary>
    public string AddLibrary(string id, string version, string assemblyPath) =>
        AddPackage(id, version, ($"lib/net10.0/{Path.GetFileName(assemblyPath)}", assemblyPath));

    /// <summary>Extracts a <c>.nupkg</c> into its package folder, as a download would.</summary>
    public string ExtractPackage(string nupkgPath)
    {
        using var reader = new PackageArchiveReader(nupkgPath);
        var identity = reader.GetIdentity();
        var directory = Layout.GetPackageDirectory(identity.Id, identity.Version);
        Directory.CreateDirectory(directory);
        foreach (var file in reader.GetFiles())
        {
            if (file.StartsWith("package/", StringComparison.Ordinal) || file.StartsWith("_rels/", StringComparison.Ordinal) || file == "[Content_Types].xml")
            {
                continue;
            }

            var target = Path.Combine(directory, file);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var source = reader.GetStream(file);
            using var output = File.Create(target);
            source.CopyTo(output);
        }

        return directory;
    }

    /// <summary>
    /// Installs plugins (and their plugin dependencies) from fixture packages without a feed: extracts them into
    /// the lowercase layout and writes their state records (<see cref="LocalPackageInstaller"/>).
    /// </summary>
    public Task<IReadOnlyList<PluginState>> InstallAsync(HostInventory host, IEnumerable<string> packages, params string[] pluginIds) =>
        new LocalPackageInstaller(host, Root).InstallAsync(packages, pluginIds, TestContext.Current.CancellationToken);

    /// <summary>Writes the state file (taking the root lock).</summary>
    public async Task WriteStateAsync(StateDocument state)
    {
        using var rootLock = await RootLock.AcquireAsync(Root);
        new StateStore(Root).Write(rootLock, state);
    }

    public void Dispose() => _root.DisposeBestEffort();   // cells keep the root's assemblies loaded
}

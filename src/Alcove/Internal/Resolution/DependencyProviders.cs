using System.Collections.Concurrent;
using Alcove.Internal.Feeds;
using Alcove.Internal.IO;
using NuGet.Configuration;
using NuGet.DependencyResolver;
using NuGet.Frameworks;
using NuGet.LibraryModel;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using INuGetLogger = NuGet.Common.ILogger;

namespace Alcove.Internal.Resolution;

/// <summary>Turns a package's declared dependencies (nearest group) into the walker's, dropping framework-provided ones.</summary>
internal static class DeclaredDependencies
{
    public static LibraryDependencyInfo ToLibraryInfo(LibraryIdentity identity, PackageMetadata? metadata, NuGetFramework framework, Func<string, VersionRange, bool> isFrameworkProvided)
    {
        if (metadata is null)
        {
            return LibraryDependencyInfo.CreateUnresolved(identity, framework);
        }

        var dependencies = new List<LibraryDependency>();
        foreach (var dependency in metadata.DependenciesFor(framework))
        {
            // Pruned like the SDK does (PrunePackageReference): the shared framework provides these.
            if (isFrameworkProvided(dependency.Id, dependency.VersionRange))
            {
                continue;
            }

            dependencies.Add(new LibraryDependency
            {
                LibraryRange = new LibraryRange(dependency.Id, dependency.VersionRange, LibraryDependencyTarget.Package),
                IncludeType = (dependency.Include.Count > 0 ? LibraryIncludeFlagUtils.GetFlags(dependency.Include) : LibraryIncludeFlags.All)
                    & ~LibraryIncludeFlagUtils.GetFlags(dependency.Exclude),
            });
        }

        return LibraryDependencyInfo.Create(identity, framework, dependencies);
    }

    public static LibraryIdentity? BestMatch(LibraryRange range, IEnumerable<NuGetVersion> versions)
    {
        // FindBestMatch = NuGet's "lowest applicable version" (highest for floating ranges).
        var best = (range.VersionRange ?? VersionRange.All).FindBestMatch(versions);
        return best is null ? null : new LibraryIdentity(range.Name, best, LibraryType.Package);
    }
}

/// <summary>A feed as a walker source (an <see cref="IRemoteDependencyProvider"/> over the session's caches).</summary>
internal sealed class FeedDependencyProvider(FeedSession session, SourceRepository repository, Func<string, VersionRange, bool> isFrameworkProvided) : IRemoteDependencyProvider
{
    public bool IsHttp => repository.PackageSource.IsHttp;

    public PackageSource Source => repository.PackageSource;

    public SourceRepository SourceRepository => repository;

    public async Task<IEnumerable<NuGetVersion>> GetAllVersionsAsync(string id, SourceCacheContext cacheContext, INuGetLogger logger, CancellationToken token) =>
        await session.GetVersionsAsync(repository, id).ConfigureAwait(false);

    public async Task<LibraryIdentity> FindLibraryAsync(LibraryRange libraryRange, NuGetFramework targetFramework, SourceCacheContext cacheContext, INuGetLogger logger, CancellationToken cancellationToken) =>
        DeclaredDependencies.BestMatch(libraryRange, await session.GetVersionsAsync(repository, libraryRange.Name).ConfigureAwait(false))!;

    public async Task<LibraryDependencyInfo> GetDependenciesAsync(LibraryIdentity libraryIdentity, NuGetFramework targetFramework, SourceCacheContext cacheContext, INuGetLogger logger, CancellationToken cancellationToken)
    {
        var package = await session.FetchAsync(repository, new PackageIdentity(libraryIdentity.Name, libraryIdentity.Version)).ConfigureAwait(false);
        return DeclaredDependencies.ToLibraryInfo(libraryIdentity, package?.Metadata, targetFramework, isFrameworkProvided);
    }

    public Task<IPackageDownloader> GetPackageDownloaderAsync(PackageIdentity packageIdentity, SourceCacheContext cacheContext, INuGetLogger logger, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Packages are downloaded through the feed session.");

    /// <summary>The package's metadata (downloading it if the walk didn't).</summary>
    public async Task<FeedPackage?> FetchAsync(PackageIdentity identity) => await session.FetchAsync(repository, identity).ConfigureAwait(false);
}

/// <summary>
/// The packages already extracted into the plugin root, as a walker source: reused instead of downloaded again,
/// and still available if a feed drops a version.
/// </summary>
internal sealed class InstalledDependencyProvider(PackageLayout layout, Func<string, VersionRange, bool> isFrameworkProvided) : IRemoteDependencyProvider
{
    private readonly VersionFolderPathResolver _paths = new(layout.PackagesDirectory, isLowercase: true);
    private readonly ConcurrentDictionary<PackageIdentity, PackageMetadata?> _metadata = new(PackageIdentityComparer.Default);
    private readonly SourceRepository _repository = Repository.Factory.GetCoreV3(new PackageSource(layout.PackagesDirectory, "plugin root"));

    public bool IsHttp => false;

    public PackageSource Source => _repository.PackageSource;

    public SourceRepository SourceRepository => _repository;

    /// <summary>Whether a package is completely extracted (its <c>.nupkg.metadata</c>, written last, exists).</summary>
    public bool IsExtracted(string id, NuGetVersion version) => File.Exists(_paths.GetNupkgMetadataPath(id, version));

    public Task<IEnumerable<NuGetVersion>> GetAllVersionsAsync(string id, SourceCacheContext cacheContext, INuGetLogger logger, CancellationToken token) =>
        Task.FromResult(Versions(id));

    public Task<LibraryIdentity> FindLibraryAsync(LibraryRange libraryRange, NuGetFramework targetFramework, SourceCacheContext cacheContext, INuGetLogger logger, CancellationToken cancellationToken) =>
        Task.FromResult(DeclaredDependencies.BestMatch(libraryRange, Versions(libraryRange.Name))!);

    public Task<LibraryDependencyInfo> GetDependenciesAsync(LibraryIdentity libraryIdentity, NuGetFramework targetFramework, SourceCacheContext cacheContext, INuGetLogger logger, CancellationToken cancellationToken) =>
        Task.FromResult(DeclaredDependencies.ToLibraryInfo(libraryIdentity, GetMetadata(libraryIdentity.Name, libraryIdentity.Version), targetFramework, isFrameworkProvided));

    public Task<IPackageDownloader> GetPackageDownloaderAsync(PackageIdentity packageIdentity, SourceCacheContext cacheContext, INuGetLogger logger, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Packages in the plugin root are already extracted.");

    /// <summary>An extracted package's <c>.nuspec</c> and manifest, or <see langword="null"/> if it isn't extracted.</summary>
    public PackageMetadata? GetMetadata(string id, NuGetVersion version) =>
        _metadata.GetOrAdd(new PackageIdentity(id, version), identity =>
        {
            if (!IsExtracted(identity.Id, identity.Version))
            {
                return null;
            }

            var nuspec = _paths.GetManifestFilePath(identity.Id, identity.Version);
            return File.Exists(nuspec)
                ? PackageMetadata.ReadExtracted(nuspec, layout.GetManifestPath(identity.Id, identity.Version), "plugin root")
                : null;
        });

    private IEnumerable<NuGetVersion> Versions(string id)
    {
        var directory = _paths.GetVersionListPath(id);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        return Directory.EnumerateDirectories(directory)
            .Select(d => NuGetVersion.TryParse(Path.GetFileName(d), out var version) ? version : null)
            .OfType<NuGetVersion>()
            .Where(v => IsExtracted(id, v))
            .ToList();
    }
}

/// <summary>
/// Pinned packages as a local walker source: the host's packages (at the host's version) and plugins outside the
/// cell (at their installed or chosen version). They are leaves: the host's own dependencies are the host's
/// business, and a plugin in another cell resolves its dependencies there.
/// </summary>
internal sealed class PinnedDependencyProvider(IReadOnlyDictionary<string, NuGetVersion> pins) : IRemoteDependencyProvider
{
    private readonly SourceRepository _repository = Repository.Factory.GetCoreV3(new PackageSource("alcove-pins", "pinned packages"));

    public bool IsHttp => false;

    public PackageSource Source => _repository.PackageSource;

    public SourceRepository SourceRepository => _repository;

    public Task<IEnumerable<NuGetVersion>> GetAllVersionsAsync(string id, SourceCacheContext cacheContext, INuGetLogger logger, CancellationToken token) =>
        Task.FromResult<IEnumerable<NuGetVersion>>(pins.TryGetValue(id, out var version) ? [version] : []);

    public Task<LibraryIdentity> FindLibraryAsync(LibraryRange libraryRange, NuGetFramework targetFramework, SourceCacheContext cacheContext, INuGetLogger logger, CancellationToken cancellationToken) =>
        Task.FromResult(pins.TryGetValue(libraryRange.Name, out var version) && (libraryRange.VersionRange?.Satisfies(version) ?? true)
            ? new LibraryIdentity(libraryRange.Name, version, LibraryType.Package)
            : null!);

    public Task<LibraryDependencyInfo> GetDependenciesAsync(LibraryIdentity libraryIdentity, NuGetFramework targetFramework, SourceCacheContext cacheContext, INuGetLogger logger, CancellationToken cancellationToken) =>
        Task.FromResult(LibraryDependencyInfo.Create(libraryIdentity, targetFramework, []));

    public Task<IPackageDownloader> GetPackageDownloaderAsync(PackageIdentity packageIdentity, SourceCacheContext cacheContext, INuGetLogger logger, CancellationToken cancellationToken) =>
        throw new NotSupportedException($"The pinned package {packageIdentity} is never downloaded.");
}

/// <summary>The synthetic project at the root of a walk: it depends on the cell's plugins and on every pinned package.</summary>
internal sealed class RootProvider(string name, IReadOnlyList<LibraryDependency> dependencies) : IDependencyProvider
{
    public bool SupportsType(LibraryDependencyTarget libraryTypeFlag) => (libraryTypeFlag & LibraryDependencyTarget.Project) != 0;

    public Library? GetLibrary(LibraryRange libraryRange, NuGetFramework targetFramework) => GetLibrary(libraryRange, targetFramework, rootPath: null);

    public Library? GetLibrary(LibraryRange libraryRange, NuGetFramework targetFramework, string? rootPath) =>
        string.Equals(libraryRange.Name, name, StringComparison.Ordinal)
            ? new Library
            {
                LibraryRange = libraryRange,
                Identity = new LibraryIdentity(name, new NuGetVersion(1, 0, 0), LibraryType.Project),
                Dependencies = dependencies,
                Resolved = true,
            }
            : null;
}

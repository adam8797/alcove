using System.Collections.Concurrent;
using Alcove.Internal.Trust;
using Microsoft.Extensions.Logging;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using INuGetLogger = NuGet.Common.ILogger;

namespace Alcove.Internal.Feeds;

/// <summary>A package downloaded from a feed into the session's temporary folder, with what the installer reads from it.</summary>
/// <param name="Identity">The package ID and version.</param>
/// <param name="Source">The feed it came from.</param>
/// <param name="NupkgPath">The downloaded <c>.nupkg</c> (deleted with the session).</param>
/// <param name="Metadata">Its dependencies, package types and plugin manifest.</param>
internal sealed record FeedPackage(PackageIdentity Identity, SourceRepository Source, string NupkgPath, PackageMetadata Metadata);

/// <summary>
/// One search, version query or install: the feeds the trust policy allows, a <see cref="SourceCacheContext"/> that
/// bypasses NuGet's user-wide HTTP cache, a temporary folder for downloads, and caches so each version list and
/// package is fetched once. Dispose it to delete the downloads.
/// </summary>
internal sealed class FeedSession : IDisposable
{
    private readonly ConcurrentDictionary<(SourceRepository, string), Lazy<Task<IReadOnlyList<NuGetVersion>>>> _versions = new();
    private readonly ConcurrentDictionary<(SourceRepository, PackageIdentity), Lazy<Task<FeedPackage?>>> _packages = new();
    private readonly CancellationToken _cancellationToken;

    public FeedSession(FeedConfiguration feeds, IPackageVerifier verifier, ILogger logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(feeds);
        ArgumentNullException.ThrowIfNull(verifier);
        _cancellationToken = cancellationToken;
        Logger = logger;
        NuGetLogger = new NuGetLogger(logger);
        TempDirectory = Path.Combine(Path.GetTempPath(), "alcove-" + Guid.NewGuid().ToString("N"));
        Cache = new SourceCacheContext { NoCache = true, GeneratedTempFolder = Path.Combine(TempDirectory, "http") };

        // The trust policy's feed check, before anything is downloaded from a feed.
        var allowed = new List<SourceRepository>();
        var issues = new List<(string Source, TrustIssue Issue)>();
        foreach (var repository in feeds.Repositories)
        {
            var decision = verifier.CheckFeed(repository.PackageSource.Source);
            issues.AddRange(decision.Issues.Select(i => (repository.PackageSource.Source, i)));
            if (decision.Allowed)
            {
                allowed.Add(repository);
            }
            else
            {
                FeedLog.FeedNotAllowed(logger, repository.PackageSource.Source,
                    string.Join(" ", decision.Issues.Select(i => i.Message)) is { Length: > 0 } reason ? reason : "not an allowed feed");
            }
        }

        Repositories = allowed;
        FeedIssues = issues;
        DisallowedFeeds = feeds.Repositories.Except(allowed).Select(r => r.PackageSource.Source).ToList();
    }

    /// <summary>The feeds the trust policy allows, in configuration order.</summary>
    public IReadOnlyList<SourceRepository> Repositories { get; }

    /// <summary>Feeds the trust policy rejected.</summary>
    public IReadOnlyList<string> DisallowedFeeds { get; }

    /// <summary>What the feed checks reported.</summary>
    public IReadOnlyList<(string Source, TrustIssue Issue)> FeedIssues { get; }

    public SourceCacheContext Cache { get; }

    public INuGetLogger NuGetLogger { get; }

    public ILogger Logger { get; }

    /// <summary>The session's temporary folder (downloads, NuGet's HTTP responses).</summary>
    public string TempDirectory { get; }

    /// <summary>Every version of a package on one feed (empty if the feed doesn't have it).</summary>
    /// <exception cref="FeedException">The feed failed (unreachable, unauthorized, ...).</exception>
    public Task<IReadOnlyList<NuGetVersion>> GetVersionsAsync(SourceRepository repository, string packageId) =>
        !IO.PackageLayout.IsValidPackageId(packageId)
            ? Task.FromResult<IReadOnlyList<NuGetVersion>>([])   // never a path or a feed query
            : _versions.GetOrAdd((repository, packageId.ToLowerInvariant()), key => new Lazy<Task<IReadOnlyList<NuGetVersion>>>(() => LoadVersionsAsync(key.Item1, packageId))).Value;

    /// <summary>Every version of a package on any allowed feed, with the first feed that has it.</summary>
    public async Task<IReadOnlyList<(NuGetVersion Version, SourceRepository Source)>> GetVersionsAsync(string packageId)
    {
        var found = new Dictionary<NuGetVersion, SourceRepository>();
        foreach (var repository in Repositories)
        {
            foreach (var version in await GetVersionsAsync(repository, packageId).ConfigureAwait(false))
            {
                found.TryAdd(version, repository);
            }
        }

        return found.OrderBy(f => f.Key).Select(f => (f.Key, f.Value)).ToList();
    }

    /// <summary>Downloads a package from a feed (once per session) and reads its metadata.</summary>
    /// <returns>The package, or <see langword="null"/> if the feed doesn't have it.</returns>
    /// <exception cref="FeedException">The feed failed, or the package can't be read.</exception>
    public Task<FeedPackage?> FetchAsync(SourceRepository repository, PackageIdentity identity) =>
        !IO.PackageLayout.IsValidPackageId(identity.Id)
            ? Task.FromResult<FeedPackage?>(null)
            : _packages.GetOrAdd((repository, identity), key => new Lazy<Task<FeedPackage?>>(() => DownloadAsync(key.Item1, key.Item2))).Value;

    public void Dispose()
    {
        Cache.Dispose();
        try
        {
            if (Directory.Exists(TempDirectory))
            {
                Directory.Delete(TempDirectory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temporary folder is harmless.
        }
    }

    private async Task<IReadOnlyList<NuGetVersion>> LoadVersionsAsync(SourceRepository repository, string packageId)
    {
        try
        {
            var resource = await repository.GetResourceAsync<FindPackageByIdResource>(_cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("it isn't a NuGet V3 feed or a package folder");
            var versions = await resource.GetAllVersionsAsync(packageId, Cache, NuGetLogger, _cancellationToken).ConfigureAwait(false);
            return versions?.Distinct().OrderBy(v => v).ToList() ?? [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not FeedException)
        {
            throw FeedException.From(repository, ex);
        }
    }

    private async Task<FeedPackage?> DownloadAsync(SourceRepository repository, PackageIdentity identity)
    {
        try
        {
            var resource = await repository.GetResourceAsync<FindPackageByIdResource>(_cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("it isn't a NuGet V3 feed or a package folder");
            if (!await resource.DoesPackageExistAsync(identity.Id, identity.Version, Cache, NuGetLogger, _cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            Directory.CreateDirectory(TempDirectory);
            var path = Path.Combine(TempDirectory, $"{identity.Id.ToLowerInvariant()}.{identity.Version.ToNormalizedString().ToLowerInvariant()}.{Guid.NewGuid():N}.nupkg");
            using (var downloader = await resource.GetPackageDownloaderAsync(identity, Cache, NuGetLogger, _cancellationToken).ConfigureAwait(false))
            {
                if (downloader is null || !await downloader.CopyNupkgFileToAsync(path, _cancellationToken).ConfigureAwait(false))
                {
                    return null;
                }
            }

            using var reader = new PackageArchiveReader(path);
            var actual = reader.GetIdentity();
            if (!string.Equals(actual.Id, identity.Id, StringComparison.OrdinalIgnoreCase) || actual.Version != identity.Version)
            {
                throw new InvalidDataException($"The feed returned {actual} for {identity}.");
            }

            var metadata = PackageMetadata.Read(reader, repository.PackageSource.Source);
            if (metadata.DependencyGroups.SelectMany(g => g.Packages).FirstOrDefault(d => !IO.PackageLayout.IsValidPackageId(d.Id)) is { } invalid)
            {
                throw new InvalidDataException($"{actual} declares a dependency with an invalid package ID: '{invalid.Id}'.");
            }

            return new FeedPackage(actual, repository, path, metadata);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not FeedException)
        {
            throw FeedException.From(repository, ex, identity);
        }
    }
}

/// <summary>A feed failed: unreachable, unauthorized, or it served something unreadable.</summary>
internal sealed class FeedException(string message, Exception? innerException) : Exception(message, innerException)
{
    /// <summary>The feed error in an exception or its inner exceptions (the walker may wrap it), or <see langword="null"/>.</summary>
    public static FeedException? Find(Exception exception) => exception switch
    {
        FeedException feed => feed,
        AggregateException aggregate => aggregate.InnerExceptions.Select(Find).FirstOrDefault(f => f is not null),
        { InnerException: { } inner } => Find(inner),
        _ => null,
    };

    public static FeedException From(SourceRepository repository, Exception exception, PackageIdentity? package = null)
    {
        var inner = exception;
        while (inner.InnerException is not null && inner is NuGet.Protocol.Core.Types.FatalProtocolException)
        {
            inner = inner.InnerException;
        }

        var what = package is null ? "" : $" while getting {package}";
        var detail = ReferenceEquals(inner, exception) ? exception.Message : $"{exception.Message} {inner.Message}";
        return new FeedException($"The feed '{repository.PackageSource.Source}' failed{what}: {detail}", exception);
    }
}

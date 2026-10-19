using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.DependencyResolver;
using NuGet.Frameworks;
using NuGet.LibraryModel;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace Spike;

/// <summary>NuGet ILogger that prints warnings and errors (and optionally everything) to the console.</summary>
public sealed class ConsoleNuGetLogger(LogLevel min = LogLevel.Warning) : LoggerBase(min)
{
    public override void Log(ILogMessage message)
    {
        if (message.Level >= VerbosityLevel) Console.WriteLine($"      [nuget {message.Level}] {message.Code} {message.Message}");
    }
    public override Task LogAsync(ILogMessage message) { Log(message); return Task.CompletedTask; }
}

public static class Feeds
{
    public const string NuGetOrg = "https://api.nuget.org/v3/index.json";

    public static string SpikeDir { get; } = FindSpikeDir();
    public static string WorkDir => Path.Combine(SpikeDir, "work");
    public static string LocalFeed => Path.Combine(WorkDir, "feed");

    static string FindSpikeDir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "build-fixtures.sh"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("spike dir not found");
    }

    /// <summary>Repository.Factory.GetCoreV3 works for both http(s) v3 feeds and local folders (flat or v3 layout).</summary>
    public static SourceRepository Repo(string source) => Repository.Factory.GetCoreV3(new PackageSource(source));
}

/// <summary>
/// Minimal IRemoteDependencyProvider over a SourceRepository using FindPackageByIdResource — enough for
/// RemoteDependencyWalker, without referencing NuGet.Commands (which has SourceRepositoryDependencyProvider).
/// Optionally prunes framework-provided packages the way the .NET 10 SDK's PrunePackageReference does.
/// </summary>
public sealed class FeedDependencyProvider(SourceRepository repo, Func<string, VersionRange, bool>? prune = null) : IRemoteDependencyProvider
{
    FindPackageByIdResource? _res;
    public List<string> Pruned { get; } = new();

    async Task<FindPackageByIdResource> Res(CancellationToken ct) => _res ??= await repo.GetResourceAsync<FindPackageByIdResource>(ct);

    public bool IsHttp => repo.PackageSource.IsHttp;
    public PackageSource Source => repo.PackageSource;
    public SourceRepository SourceRepository => repo;

    public async Task<IEnumerable<NuGetVersion>> GetAllVersionsAsync(string id, SourceCacheContext cache, ILogger logger, CancellationToken ct)
        => await (await Res(ct)).GetAllVersionsAsync(id, cache, logger, ct);

    public async Task<LibraryIdentity?> FindLibraryAsync(LibraryRange range, NuGetFramework framework, SourceCacheContext cache, ILogger logger, CancellationToken ct)
    {
        var versions = await GetAllVersionsAsync(range.Name, cache, logger, ct);
        // FindBestMatch = NuGet's "lowest applicable version" for non-floating ranges (and highest for floating ones).
        var best = range.VersionRange?.FindBestMatch(versions);
        return best is null ? null : new LibraryIdentity(range.Name, best, LibraryType.Package);
    }

    public async Task<LibraryDependencyInfo> GetDependenciesAsync(LibraryIdentity id, NuGetFramework framework, SourceCacheContext cache, ILogger logger, CancellationToken ct)
    {
        var info = await (await Res(ct)).GetDependencyInfoAsync(id.Name, id.Version, cache, logger, ct);
        if (info is null) return LibraryDependencyInfo.CreateUnresolved(id, framework);
        var group = NuGetFrameworkUtility.GetNearest(info.DependencyGroups, framework, g => g.TargetFramework);
        var deps = new List<LibraryDependency>();
        foreach (var d in group?.Packages ?? [])
        {
            if (prune is not null && prune(d.Id, d.VersionRange)) { lock (Pruned) Pruned.Add($"{d.Id} {d.VersionRange} (from {id.Name} {id.Version})"); continue; }
            deps.Add(new LibraryDependency
            {
                LibraryRange = new LibraryRange(d.Id, d.VersionRange, LibraryDependencyTarget.Package),
                IncludeType = (d.Include.Count > 0 ? LibraryIncludeFlagUtils.GetFlags(d.Include) : LibraryIncludeFlags.All) & ~LibraryIncludeFlagUtils.GetFlags(d.Exclude),
            });
        }
        return LibraryDependencyInfo.Create(id, framework, deps);
    }

    public async Task<IPackageDownloader> GetPackageDownloaderAsync(PackageIdentity id, SourceCacheContext cache, ILogger logger, CancellationToken ct)
        => await (await Res(ct)).GetPackageDownloaderAsync(id, cache, logger, ct);
}

/// <summary>
/// Presents the host's deps.json packages as an already-present local source: exactly one version per id,
/// dependencies as recorded in deps.json. Never downloads anything.
/// </summary>
public sealed class HostDependencyProvider(HostInventory host, ISet<string>? excluded = null) : IRemoteDependencyProvider
{
    static readonly SourceRepository Fake = Repository.Factory.GetCoreV3(new PackageSource("host-deps-json", "host"));
    public bool IsHttp => false;
    public PackageSource Source => Fake.PackageSource;
    public SourceRepository SourceRepository => Fake;

    HostPackage? Get(string id) => excluded?.Contains(id) == true ? null : host.Packages.TryGetValue(id, out var p) ? p : host.Projects.TryGetValue(id, out var q) ? q : null;

    public Task<IEnumerable<NuGetVersion>> GetAllVersionsAsync(string id, SourceCacheContext c, ILogger l, CancellationToken ct)
        => Task.FromResult<IEnumerable<NuGetVersion>>(Get(id) is { } p ? [NuGetVersion.Parse(p.Version)] : []);

    public Task<LibraryIdentity?> FindLibraryAsync(LibraryRange range, NuGetFramework f, SourceCacheContext c, ILogger l, CancellationToken ct)
    {
        var p = Get(range.Name);
        if (p is null) return Task.FromResult<LibraryIdentity?>(null);
        var v = NuGetVersion.Parse(p.Version);
        return Task.FromResult<LibraryIdentity?>(range.VersionRange is null || range.VersionRange.Satisfies(v) ? new LibraryIdentity(p.Id, v, LibraryType.Package) : null);
    }

    // Host packages are leaves: they're already present, and every host package is pinned at the root anyway,
    // so walking the host's own internal edges would only produce spurious downgrades between host packages.
    public Task<LibraryDependencyInfo> GetDependenciesAsync(LibraryIdentity id, NuGetFramework f, SourceCacheContext c, ILogger l, CancellationToken ct)
        => Task.FromResult(LibraryDependencyInfo.Create(id, f, []));

    public Task<IPackageDownloader> GetPackageDownloaderAsync(PackageIdentity id, SourceCacheContext c, ILogger l, CancellationToken ct)
        => throw new InvalidOperationException($"host package {id} is never downloaded");
}

/// <summary>The synthetic "project" at the root of a walk: depends on the plugins (and optionally pins host packages).</summary>
public sealed class RootProvider(string name, IReadOnlyList<LibraryDependency> deps) : IDependencyProvider
{
    public bool SupportsType(LibraryDependencyTarget t) => (t & LibraryDependencyTarget.Project) != 0;
    public Library? GetLibrary(LibraryRange range, NuGetFramework f) => GetLibrary(range, f, null);
    public Library? GetLibrary(LibraryRange range, NuGetFramework f, string? rootPath)
        => range.Name == name
            ? new Library { LibraryRange = range, Identity = new LibraryIdentity(name, new NuGetVersion(1, 0, 0), LibraryType.Project), Dependencies = deps, Resolved = true }
            : null;
}

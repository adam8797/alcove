using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Packaging.Signing;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace Spike;

/// <summary>Q5: download + extract into packages/&lt;id&gt;/&lt;version&gt;/ with NuGet's own hash bookkeeping.</summary>
public static class PackageStore
{
    public static string PackagesDir => Path.Combine(Feeds.WorkDir, "packages");

    /// <summary>
    /// Same layout and side files as the global packages folder:
    /// &lt;root&gt;/&lt;id lowercased&gt;/&lt;normalized version&gt;/{.nupkg, .nupkg.sha512, .nupkg.metadata, .nuspec, files}.
    /// </summary>
    public static async Task<string> EnsureAsync(PackageIdentity id, IEnumerable<SourceRepository> repos, ILogger log, CancellationToken ct = default)
    {
        var resolver = new VersionFolderPathResolver(PackagesDir, isLowercase: true);
        var dir = resolver.GetInstallPath(id.Id, id.Version);
        if (File.Exists(Path.Combine(dir, ".nupkg.metadata"))) return dir;   // the metadata file is written last = "complete" marker

        using var cache = new SourceCacheContext();
        foreach (var repo in repos)
        {
            var find = await repo.GetResourceAsync<FindPackageByIdResource>(ct);
            if (!await find.DoesPackageExistAsync(id.Id, id.Version, cache, log, ct)) continue;
            using var downloader = await find.GetPackageDownloaderAsync(id, cache, log, ct);
            var ctx = new PackageExtractionContext(PackageSaveMode.Defaultv3, XmlDocFileSaveMode.Skip,
                ClientPolicyContext.GetClientPolicy(NullSettings.Instance, log), log);
            await PackageExtractor.InstallFromSourceAsync(id, downloader, resolver, ctx, ct, Guid.Empty);
            return dir;
        }
        throw new InvalidOperationException($"{id} not found on any source");
    }
}

public static class Download
{
    public static async Task RunAsync(string[] args, ILogger log)
    {
        var nugetOrg = Feeds.Repo(Feeds.NuGetOrg);
        var local = Feeds.Repo(Feeds.LocalFeed);
        var cases = new (PackageIdentity Id, SourceRepository Repo)[]
        {
            (new("Serilog", NuGetVersion.Parse("3.1.1")), nugetOrg),               // also in the host's deps.json -> compare with its sha512
            (new("Newtonsoft.Json", NuGetVersion.Parse("13.0.3")), nugetOrg),
            (new("PluginA", NuGetVersion.Parse("1.0.0")), local),                   // unsigned, local folder feed
        };
        var host = HostInventory.FromCurrentProcess();
        foreach (var (id, repo) in cases)
        {
            Console.WriteLine($"== {id} from {repo.PackageSource.Source}");
            var dir = new VersionFolderPathResolver(PackageStore.PackagesDir, true).GetInstallPath(id.Id, id.Version);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            var sw = Stopwatch.StartNew();
            dir = await PackageStore.EnsureAsync(id, [repo], log);
            Console.WriteLine($"  extracted to {Path.GetRelativePath(Feeds.SpikeDir, dir)} in {sw.ElapsedMilliseconds} ms");
            foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Order())
            {
                var fi = new FileInfo(f);
                Console.WriteLine($"    {Path.GetRelativePath(dir, f),-60} {fi.Length,9} B  mtime {fi.LastWriteTimeUtc:yyyy-MM-dd HH:mm:ss}Z");
            }

            var nupkg = Directory.GetFiles(dir, "*.nupkg").Single();
            var fileHash = Convert.ToBase64String(SHA512.HashData(File.ReadAllBytes(nupkg)));
            var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, ".nupkg.metadata"))).RootElement;
            var sha512File = File.ReadAllText(Directory.GetFiles(dir, "*.nupkg.sha512").Single()).Trim();
            using var reader = new PackageArchiveReader(nupkg);
            var contentHash = reader.GetContentHash(CancellationToken.None);
            var signed = await reader.IsSignedAsync(CancellationToken.None);
            Console.WriteLine($"  signed: {signed}");
            Console.WriteLine($"  .nupkg.metadata     : {metadata}");
            Console.WriteLine($"  .nupkg.sha512       : {sha512File}");
            Console.WriteLine($"  SHA512(file bytes)  : {fileHash}");
            Console.WriteLine($"  reader.GetContentHash: {contentHash}");
            if (host.Packages.TryGetValue(id.Id, out var hp) && hp.Version == id.Version.ToNormalizedString())
                Console.WriteLine($"  host deps.json sha512: {hp.Sha512}");
            if (repo.PackageSource.IsHttp)
            {
                // nuget.org's own record of the hash (catalog packageHash, = SHA512 of the published (signed) .nupkg)
                var meta = await repo.GetResourceAsync<PackageMetadataResource>();
                using var cache = new SourceCacheContext();
                var m = await meta.GetMetadataAsync(id, cache, log, default);
                using var http = new HttpClient();
                var reg = $"https://api.nuget.org/v3/registration5-semver1/{id.Id.ToLowerInvariant()}/{id.Version.ToNormalizedString().ToLowerInvariant()}.json";
                var regDoc = JsonDocument.Parse(await http.GetStringAsync(reg)).RootElement;
                var catalogUrl = regDoc.GetProperty("catalogEntry").GetString()!;
                var cat = JsonDocument.Parse(await http.GetStringAsync(catalogUrl)).RootElement;
                Console.WriteLine($"  nuget.org catalog   : packageHash={cat.GetProperty("packageHash")} ({cat.GetProperty("packageHashAlgorithm")})");
            }
            Console.WriteLine($"  file hash == metadata.contentHash: {fileHash == metadata.GetProperty("contentHash").GetString()}");
        }
    }
}

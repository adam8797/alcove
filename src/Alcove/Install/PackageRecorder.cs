using System.Reflection.Metadata;
using System.Text.Json;
using Alcove.Hosting;
using Alcove.Inspection;
using Alcove.Packaging;
using Alcove.Storage;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Versioning;

namespace Alcove.Install;

/// <summary>What <see cref="PackageRecorder.Record"/> found in an extracted package folder.</summary>
/// <param name="Id">The package ID.</param>
/// <param name="Version">The package version.</param>
/// <param name="Directory">The package folder.</param>
/// <param name="ContentHash">NuGet's <c>contentHash</c>, from <c>.nupkg.metadata</c>.</param>
/// <param name="Files">Every file in the folder, with size and timestamp.</param>
/// <param name="Assemblies">The runtime assemblies selected for the host (WPF relevance not yet decided).</param>
/// <param name="Manifest">The plugin manifest, if the package is a plugin.</param>
/// <param name="Source">The feed it was extracted from, from <c>.nupkg.metadata</c>, if recorded there.</param>
internal sealed record PackageRecord(
    string Id,
    NuGetVersion Version,
    string Directory,
    string ContentHash,
    IReadOnlyList<FileRecord> Files,
    IReadOnlyList<AssemblyRecord> Assemblies,
    PluginManifest? Manifest,
    string? Source = null);

/// <summary>A resolved dependency of a plugin, and its extracted package when it is cell-private.</summary>
internal sealed record DependencyRecord(string Id, NuGetVersion Version, DependencyClass Classification, PackageRecord? Package = null)
{
    /// <summary>The ranges the plugin and its cell-private dependencies declare on this package.</summary>
    public IReadOnlyList<DependencyRequest> RequestedBy { get; init; } = [];
}

/// <summary>
/// Turns extracted packages into state file records (architecture §2): <c>contentHash</c>, each file's size and
/// timestamp, the runtime assemblies with their WPF relevance, and the plugin's exposed dependencies; then
/// writes them to <c>state.json</c> under the root lock. Resolution (which versions, which class) is the
/// caller's job. Used by the installer and by tests.
/// </summary>
internal sealed class PackageRecorder
{
    private readonly HostInventory _host;
    private readonly PackageLayout _layout;
    private readonly AssetSelector _assets;
    private readonly VersionFolderPathResolver _paths;

    public PackageRecorder(HostInventory host, PackageLayout layout, AssetSelector? assets = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(layout);
        _host = host;
        _layout = layout;
        _assets = assets ?? AssetSelector.Default;
        _paths = new VersionFolderPathResolver(layout.PackagesDirectory, isLowercase: true);
    }

    /// <summary>
    /// Extracts a <c>.nupkg</c> into <c>packages/&lt;id-lowercase&gt;/&lt;version&gt;/</c> like NuGet's global packages
    /// folder: the package files, the <c>.nuspec</c>, a copy of the <c>.nupkg</c> and, last, <c>.nupkg.metadata</c>
    /// with the <c>contentHash</c>. A folder that already has <c>.nupkg.metadata</c> is left as it is.
    /// </summary>
    /// <returns>The package's identity.</returns>
    public PackageIdentity Extract(string nupkgPath, string? source = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nupkgPath);
        using var reader = new PackageArchiveReader(nupkgPath);
        var identity = reader.GetIdentity();
        var directory = _paths.GetInstallPath(identity.Id, identity.Version);
        var metadataPath = _paths.GetNupkgMetadataPath(identity.Id, identity.Version);
        if (File.Exists(metadataPath))
        {
            return identity;
        }

        Directory.CreateDirectory(directory);
        foreach (var file in reader.GetFiles())
        {
            if (IsPackagingFile(file))
            {
                continue;
            }

            var target = Path.GetFullPath(Path.Combine(directory, file));
            if (!target.StartsWith(Path.GetFullPath(directory) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"The package {identity} contains a file outside its folder: '{file}'.");
            }

            if (file.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase) && !file.Contains('/', StringComparison.Ordinal))
            {
                target = _paths.GetManifestFilePath(identity.Id, identity.Version);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = reader.GetStream(file);
            using var output = File.Create(target);
            input.CopyTo(output);
        }

        File.Copy(nupkgPath, _paths.GetPackageFilePath(identity.Id, identity.Version), overwrite: true);
        WriteNupkgMetadata(metadataPath, ComputeContentHash(nupkgPath), source ?? Path.GetDirectoryName(Path.GetFullPath(nupkgPath)));
        return identity;
    }

    /// <summary>
    /// Extracts a downloaded (and already verified) <c>.nupkg</c> with NuGet's <see cref="PackageExtractor"/> into the
    /// lowercase layout: the files, the <c>.nuspec</c>, the <c>.nupkg</c> (kept for re-verification and re-hashing),
    /// <c>.nupkg.sha512</c> and, last, <c>.nupkg.metadata</c> with the <c>contentHash</c>. NuGet's own signature
    /// verification is off: the trust policy ran before. A folder that already has <c>.nupkg.metadata</c> is left as it is.
    /// </summary>
    /// <returns>Whether the package was extracted (<see langword="false"/>: it already was).</returns>
    public async Task<bool> ExtractAsync(string nupkgPath, PackageIdentity identity, string source, NuGet.Common.ILogger logger, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nupkgPath);
        ArgumentNullException.ThrowIfNull(identity);
        if (File.Exists(_paths.GetNupkgMetadataPath(identity.Id, identity.Version)))
        {
            return false;
        }

        var context = new PackageExtractionContext(PackageSaveMode.Defaultv3, XmlDocFileSaveMode.Skip, clientPolicyContext: null, logger);
        return await PackageExtractor.InstallFromSourceAsync(
            source,
            identity,
            async destination =>
            {
                await using var input = File.OpenRead(nupkgPath);
                await input.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            },
            _paths,
            context,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>NuGet's <c>contentHash</c> of a package: base64 SHA-512, computed without the signature.</summary>
    public static string ComputeContentHash(string nupkgPath)
    {
        using var reader = new PackageArchiveReader(nupkgPath);
        return reader.GetContentHash(CancellationToken.None);
    }

    /// <summary>Records an extracted package: hash, files, the host's runtime assemblies, and the manifest.</summary>
    /// <exception cref="DirectoryNotFoundException">The package isn't extracted.</exception>
    /// <exception cref="InvalidDataException">The folder has no (valid) <c>.nupkg.metadata</c>.</exception>
    public PackageRecord Record(string id, NuGetVersion version)
    {
        var directory = _layout.GetPackageDirectory(id, version);
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"The package {id} {version} isn't extracted to '{directory}'.");
        }

        var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Select(f => new FileInfo(f))
            .Select(f => new FileRecord(Path.GetRelativePath(directory, f.FullName).Replace('\\', '/'), f.Length, f.LastWriteTimeUtc))
            .OrderBy(f => f.Path, StringComparer.Ordinal)
            .ToList();

        var assets = _assets.SelectFromDirectory(directory, _host.Framework, _host.RuntimeIdentifier);
        var assemblies = assets.RuntimeAssemblies
            .Select(relative => new AssemblyRecord
            {
                Name = Path.GetFileNameWithoutExtension(relative),
                Path = relative,
                AssemblyVersion = ReadAssemblyVersion(Path.Combine(directory, relative)),
            })
            .ToList();

        var manifestPath = _layout.GetManifestPath(id, version);
        PluginManifest? manifest = null;
        if (File.Exists(manifestPath))
        {
            using var stream = File.OpenRead(manifestPath);
            manifest = PluginManifest.Read(stream);
        }

        var metadataPath = _paths.GetNupkgMetadataPath(id, version);
        return new PackageRecord(id, version, directory, ReadContentHash(metadataPath), files, assemblies, manifest, ReadSource(metadataPath));
    }

    /// <summary>
    /// The state file entry of a plugin: its own record, its resolved dependency set, the WPF relevance of its and
    /// its cell-private dependencies' assemblies, and its exposed dependencies (a metadata scan of its public API).
    /// </summary>
    public PluginState CreatePluginState(PackageRecord plugin, IReadOnlyList<DependencyRecord> dependencies)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(dependencies);
        if (plugin.Manifest is null)
        {
            throw new InvalidOperationException($"{plugin.Id} {plugin.Version} has no plugin manifest, so it isn't a plugin package.");
        }

        var pluginPaths = plugin.Assemblies.Select(a => Path.Combine(plugin.Directory, a.Path)).ToList();
        var privatePaths = dependencies
            .Where(d => d.Classification == DependencyClass.CellPrivate && d.Package is not null)
            .SelectMany(d => d.Package!.Assemblies.Select(a => Path.Combine(d.Package.Directory, a.Path)));
        var wpfRelevant = WpfRelevance.FindRelevant(pluginPaths.Concat(privatePaths).Select(WpfAssemblyInspector.Inspect).OfType<WpfAssemblyInfo>());
        var exposed = ExposedDependencyScanner.Scan(pluginPaths, _host.IsHostOwnedAssembly);

        return new PluginState
        {
            Id = plugin.Id,
            Version = plugin.Version,
            ContentHash = plugin.ContentHash,
            Source = plugin.Source,
            Files = plugin.Files,
            Assemblies = WithRelevance(plugin.Assemblies, wpfRelevant),
            Dependencies = dependencies.Select(d => new ResolvedDependency
            {
                Id = d.Id,
                Version = d.Version,
                Classification = d.Classification,
                ContentHash = d.Package?.ContentHash,
                Source = d.Package?.Source,
                Files = d.Package?.Files ?? [],
                Assemblies = d.Package is null ? [] : WithRelevance(d.Package.Assemblies, wpfRelevant),
                RequestedBy = d.RequestedBy,
            }).ToList(),
            ExposedDependencies = [.. exposed.AssemblyNames.Order(StringComparer.OrdinalIgnoreCase)],
            Cell = plugin.Manifest.Cell,
            ManifestSchemaVersion = plugin.Manifest.SchemaVersion,
        };
    }

    /// <summary>Adds or replaces plugins (by ID) in the state file, under the root lock, re-reading it first.</summary>
    public async Task<StateDocument> WriteAsync(IEnumerable<PluginState> plugins, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plugins);
        using var rootLock = await RootLock.AcquireAsync(_layout.Root, cancellationToken: cancellationToken).ConfigureAwait(false);
        return Write(rootLock, plugins);
    }

    /// <summary>Adds or replaces plugins (by ID) in the state file while the caller holds the root lock, re-reading it first.</summary>
    public StateDocument Write(RootLock rootLock, IEnumerable<PluginState> plugins)
    {
        ArgumentNullException.ThrowIfNull(rootLock);
        ArgumentNullException.ThrowIfNull(plugins);
        var store = new StateStore(_layout.Root);
        var updated = Merge(store.Read(), plugins);
        store.Write(rootLock, updated);
        return updated;
    }

    /// <summary>A state with plugins added or replaced (by ID); replaced entries keep their position.</summary>
    public static StateDocument Merge(StateDocument current, IEnumerable<PluginState> plugins)
    {
        ArgumentNullException.ThrowIfNull(current);
        var added = plugins.ToList();
        return current with
        {
            Plugins = current.Plugins
                .Select(p => added.FirstOrDefault(a => string.Equals(a.Id, p.Id, StringComparison.OrdinalIgnoreCase)) ?? p)
                .Concat(added.Where(a => !current.Plugins.Any(p => string.Equals(a.Id, p.Id, StringComparison.OrdinalIgnoreCase))))
                .ToList(),
        };
    }

    private static IReadOnlyList<AssemblyRecord> WithRelevance(IReadOnlyList<AssemblyRecord> assemblies, IReadOnlySet<string> relevant) =>
        assemblies.Select(a => a with { WpfRelevant = relevant.Contains(a.Name) }).ToList();

    /// <summary>Whether a package entry is NuGet packaging (OPC parts, the signature) rather than package content.</summary>
    internal static bool IsPackagingFile(string file) =>
        file.StartsWith("package/", StringComparison.Ordinal)
        || file.StartsWith("_rels/", StringComparison.Ordinal)
        || string.Equals(file, "[Content_Types].xml", StringComparison.Ordinal)
        || (file.EndsWith(".signature.p7s", StringComparison.OrdinalIgnoreCase) && !file.Contains('/', StringComparison.Ordinal));

    private static string? ReadAssemblyVersion(string path)
    {
        using var pe = MetadataFile.TryOpen(path, out _);
        return pe is null ? null : AssemblyIdentity.FromDefinition(pe.GetMetadataReader()).Version.ToString();
    }

    private static void WriteNupkgMetadata(string path, string contentHash, string? source)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", 2);
            writer.WriteString("contentHash", contentHash);
            writer.WriteString("source", source);
            writer.WriteEndObject();
        }

        AtomicFile.WriteAllBytes(path, buffer.ToArray());
    }

    /// <summary>
    /// The <c>source</c> in a <c>.nupkg.metadata</c> file: the feed NuGet's extraction (or <see cref="Extract"/>)
    /// recorded; <see langword="null"/> if the file is missing, invalid or has none.
    /// </summary>
    internal static string? ReadSource(string metadataPath)
    {
        try
        {
            using var document = JsonDocument.Parse(AtomicFile.ReadAllBytes(metadataPath));
            return document.RootElement.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.String
                && source.GetString() is { Length: > 0 } value
                ? value
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>The <c>contentHash</c> in a <c>.nupkg.metadata</c> file.</summary>
    /// <exception cref="InvalidDataException">The file is missing or has no hash.</exception>
    internal static string ReadContentHash(string metadataPath)
    {
        try
        {
            using var document = JsonDocument.Parse(AtomicFile.ReadAllBytes(metadataPath));
            return document.RootElement.TryGetProperty("contentHash", out var hash) && hash.GetString() is { Length: > 0 } value
                ? value
                : throw new InvalidDataException($"'{metadataPath}' has no contentHash.");
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or JsonException)
        {
            throw new InvalidDataException($"'{metadataPath}' is missing or invalid, so the package's contentHash is unknown.", ex);
        }
    }
}

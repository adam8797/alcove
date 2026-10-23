using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using Alcove.Cells;
using Alcove.Hosting;
using Alcove.Inspection;
using Alcove.Packaging;
using Alcove.Storage;
using Alcove.Validation;
using Microsoft.Extensions.Logging;
using NuGet.Versioning;

namespace Alcove.Development;

/// <summary>
/// A development plugin as read from its build output: the state entry an install would have recorded (but never
/// written), its manifest, and where each of its cell-private packages' assets are in the build output.
/// </summary>
/// <param name="Source">What the app asked for.</param>
/// <param name="Directory">The build output folder (absolute).</param>
/// <param name="State">The would-be state entry: identity, own assemblies, classified dependency set, exposed dependencies.</param>
/// <param name="Manifest">The manifest, or <see langword="null"/> if it couldn't be read (then an error blocks the plugin).</param>
/// <param name="Assets">The assets of the plugin itself (by its ID) and of each cell-private package, relative to <paramref name="Directory"/>.</param>
internal sealed record DevelopmentBuild(
    DevelopmentPlugin Source,
    string Directory,
    PluginState State,
    PluginManifest? Manifest,
    IReadOnlyDictionary<string, PackageAssets> Assets)
{
    public string Id => State.Id;

    /// <summary>Where a package's assets are for this plugin, or <see langword="null"/> if it's not one of its cell-private packages.</summary>
    public LocatedAssets? Locate(string packageId) =>
        Assets.TryGetValue(packageId, out var assets) ? new LocatedAssets(Directory, assets) : null;
}

/// <summary>
/// Reads development plugins from their build output (ADR 0017): the manifest <c>Alcove.Sdk</c> writes there, and the
/// plugin's <c>deps.json</c>, which replaces NuGet resolution. Each dependency is classified as an install would
/// classify it against this host; where the build differs from what an install would load, a warning says so.
/// </summary>
internal sealed class DevelopmentPluginReader
{
    private const int MaxFilesListed = 3;

    private readonly HostInventory _host;
    private readonly AssetSelector _assets;
    private readonly ILogger _logger;

    public DevelopmentPluginReader(HostInventory host, AssetSelector assets, ILogger logger)
    {
        _host = host;
        _assets = assets;
        _logger = logger;
    }

    /// <summary>Reports a validation issue.</summary>
    public delegate void IssueSink(ValidationSeverity severity, string code, string message, string? pluginId);

    /// <summary>
    /// Reads every development plugin. One that can't be identified (no folder, no manifest) is only reported; one
    /// whose ID is known but whose build output is unusable is returned with an error reported, so it's listed as not
    /// loaded. A second folder with an ID already read is reported and skipped.
    /// </summary>
    /// <param name="plugins">The development plugins the app asked for.</param>
    /// <param name="installedIds">The IDs of the installed plugins, for plugin dependencies.</param>
    /// <param name="report">Receives the issues.</param>
    public IReadOnlyList<DevelopmentBuild> Read(IReadOnlyList<DevelopmentPlugin> plugins, IEnumerable<string> installedIds, IssueSink report)
    {
        var identified = new List<Identified>();
        foreach (var plugin in plugins)
        {
            if (Identify(plugin, report) is not { } found)
            {
                continue;
            }

            if (identified.FirstOrDefault(i => string.Equals(i.Id, found.Id, StringComparison.OrdinalIgnoreCase)) is { } first)
            {
                report(ValidationSeverity.Warning, ValidationCodes.DevelopmentPluginInvalid,
                    $"The development plugin {found.Id} is listed twice ('{first.Directory}' and '{found.Directory}'); the first is used.", found.Id);
                continue;
            }

            identified.Add(found);
        }

        var pluginIds = installedIds.Concat(identified.Select(i => i.Id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return identified.Select(i => Build(i, pluginIds, report)).ToList();
    }

    /// <summary>Finds the folder, the manifest (ID and version) and the <c>deps.json</c>.</summary>
    private static Identified? Identify(DevelopmentPlugin plugin, IssueSink report)
    {
        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(plugin.OutputDirectory));
        var label = plugin.ProjectPath is { } project ? Path.GetFileNameWithoutExtension(project) : directory;
        var where = plugin.ProjectPath is null ? $"'{directory}'" : $"'{directory}' (project '{plugin.ProjectPath}')";
        if (!System.IO.Directory.Exists(directory))
        {
            report(ValidationSeverity.Error, ValidationCodes.DevelopmentPluginInvalid,
                $"The development plugin folder {where} doesn't exist. Build the plugin first.", label);
            return null;
        }

        var manifests = System.IO.Directory.GetFiles(directory, "*" + PluginManifest.FileNameSuffix);
        var depsFiles = System.IO.Directory.GetFiles(directory, "*.deps.json");
        if (manifests.Length == 0)
        {
            report(ValidationSeverity.Error, ValidationCodes.DevelopmentPluginInvalid,
                $"The development plugin folder {where} has no plugin manifest (*{PluginManifest.FileNameSuffix}). Build the plugin with an Alcove.Sdk that writes it into the build output.", label);
            return null;
        }

        var manifestPath = manifests.Length == 1
            ? manifests[0]
            : manifests.FirstOrDefault(m => depsFiles.Any(d => string.Equals(BaseName(d, ".deps.json"), BaseName(m, PluginManifest.FileNameSuffix), StringComparison.OrdinalIgnoreCase)));
        if (manifestPath is null)
        {
            report(ValidationSeverity.Error, ValidationCodes.DevelopmentPluginInvalid,
                $"The development plugin folder {where} has several plugin manifests ({string.Join(", ", manifests.Select(Path.GetFileName))}); point to the build output of one plugin.", label);
            return null;
        }

        var fileId = BaseName(manifestPath, PluginManifest.FileNameSuffix);
        PluginManifest? manifest = null;
        string? manifestError = null;
        try
        {
            using var stream = File.OpenRead(manifestPath);
            manifest = PluginManifest.Read(stream);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            manifestError = $"Its manifest '{manifestPath}' can't be read: {ex.Message}";
        }

        var id = manifest?.PackageId ?? fileId;
        if (!PackageLayout.IsValidPackageId(id))
        {
            report(ValidationSeverity.Error, ValidationCodes.DevelopmentPluginInvalid,
                $"The development plugin in {where} has the package ID '{id}', which isn't a valid package ID.", label);
            return null;
        }

        var depsPath = depsFiles.Length == 1
            ? depsFiles[0]
            : depsFiles.FirstOrDefault(d => string.Equals(BaseName(d, ".deps.json"), id, StringComparison.OrdinalIgnoreCase));
        return new Identified(plugin, directory, where, id, manifest, manifestError, depsPath, depsFiles.Length);
    }

    /// <summary>The would-be state entry: the plugin's identity, own assemblies and classified dependency set.</summary>
    private DevelopmentBuild Build(Identified plugin, IReadOnlySet<string> pluginIds, IssueSink report)
    {
        NuGetVersion.TryParse(plugin.Manifest?.Version, out var version);
        var failed = (string message) =>
        {
            report(ValidationSeverity.Error, ValidationCodes.DevelopmentPluginInvalid, $"The development plugin {plugin.Id} in {plugin.Where} can't load: {message}", plugin.Id);
            return Empty(plugin, version ?? new NuGetVersion(0, 0, 0));
        };

        if (plugin.ManifestError is { } manifestError)
        {
            return failed(manifestError);
        }

        var manifest = plugin.Manifest!;
        if (manifest.SchemaVersion is < 1 or > PluginManifest.CurrentSchemaVersion)
        {
            report(ValidationSeverity.Error, ValidationCodes.ManifestSchemaUnsupported,
                $"The manifest of the development plugin {plugin.Id} has schema version {manifest.SchemaVersion}; this version of Alcove supports 1 to {PluginManifest.CurrentSchemaVersion}.", plugin.Id);
            return Empty(plugin, version ?? new NuGetVersion(0, 0, 0));
        }

        if (plugin.DepsPath is null)
        {
            return failed(plugin.DepsFileCount == 0
                ? "its build output has no deps.json (GenerateDependencyFile must stay on)."
                : $"its build output has several deps.json files and none is named {plugin.Id}.deps.json.");
        }

        IReadOnlyList<DepsJsonReader.Library> libraries;
        try
        {
            libraries = DepsJsonReader.ReadLibraries(plugin.DepsPath);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return failed(ex.Message);
        }

        var assemblyName = BaseName(plugin.DepsPath, ".deps.json");
        var root = libraries.FirstOrDefault(l => IsProject(l) && l.Assets.Any(a => a.AssetType == "runtime" && IsFile(a.Path, assemblyName + ".dll")))
            ?? libraries.FirstOrDefault(IsProject);
        if (root is null)
        {
            return failed($"its deps.json ('{plugin.DepsPath}') lists no project.");
        }

        if (version is null && !NuGetVersion.TryParse(root.VersionText, out version))
        {
            return failed($"neither its manifest nor its deps.json gives a valid version ('{root.VersionText}').");
        }

        var byId = new Dictionary<string, DepsJsonReader.Library>(StringComparer.OrdinalIgnoreCase);
        foreach (var library in libraries)
        {
            byId.TryAdd(library.Id, library);
        }

        // The dependency closure, as an install resolves it: plugin dependencies and host-owned packages bring their
        // own dependencies, which aren't this plugin's.
        var privateDependencies = manifest.PrivateDependencies.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pluginDependencies = (manifest.PluginDependencies ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dependencies = new List<ResolvedDependency>();
        var assets = new Dictionary<string, PackageAssets>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        var ownAssets = SelectAssets(plugin.Directory, root, missing);
        assets[plugin.Id] = ownAssets;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root.Id, plugin.Id };

        // A "reference" library (a file reference, e.g. what DisableTransitiveProjectReferences adds) only counts for
        // assemblies no package or project provides: those are classified with their package.
        var packaged = libraries
            .Where(l => !IsReference(l))
            .SelectMany(l => l.Assets.Where(a => a.AssetType == "runtime").Select(a => Path.GetFileNameWithoutExtension(FileName(a.Path))))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<(string Id, string Version, string DeclaredBy)>(root.Dependencies.Select(d => (d.Key, d.Value, plugin.Id)));
        while (pending.TryDequeue(out var next))
        {
            if (!seen.Add(next.Id) || !byId.TryGetValue(next.Id, out var library) || library.Type.Equals("runtimepack", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (IsReference(library) && library.Assets.Where(a => a.AssetType == "runtime").All(a => packaged.Contains(Path.GetFileNameWithoutExtension(FileName(a.Path)))))
            {
                continue;
            }

            if (!NuGetVersion.TryParse(library.VersionText, out var dependencyVersion) && !NuGetVersion.TryParse(next.Version, out dependencyVersion))
            {
                dependencyVersion = new NuGetVersion(0, 0, 0);
            }

            var classification = Classify(plugin.Id, library, dependencyVersion, pluginIds, pluginDependencies, privateDependencies, report);
            DevelopmentLog.Classified(_logger, plugin.Id, library.Id, dependencyVersion, classification.ToString());
            var dependency = new ResolvedDependency
            {
                Id = library.Id,
                Version = dependencyVersion,
                Classification = classification,
                Source = plugin.Directory,

                // A reference on another plugin is at least the version built against, as a PackageReference would declare.
                RequestedBy = classification == DependencyClass.Plugin
                    ? [new DependencyRequest(next.DeclaredBy, new VersionRange(dependencyVersion, includeMinVersion: true))]
                    : [],
            };

            if (classification == DependencyClass.CellPrivate)
            {
                var selected = SelectAssets(plugin.Directory, library, missing);
                assets[library.Id] = selected;
                dependency = dependency with { Assemblies = Records(selected) };
            }

            dependencies.Add(dependency);
            if (classification is DependencyClass.CellPrivate or DependencyClass.HostShared)
            {
                foreach (var (id, declared) in library.Dependencies)
                {
                    pending.Enqueue((id, declared, library.Id));
                }
            }
        }

        if (missing.Count > 0)
        {
            return failed($"{(missing.Count == 1 ? "1 file" : $"{missing.Count} files")} its deps.json lists {(missing.Count == 1 ? "is" : "are")} missing from the build output: {string.Join(", ", missing.Take(MaxFilesListed))}{(missing.Count > MaxFilesListed ? ", ..." : "")}. Rebuild it.");
        }

        // WPF relevance and exposed dependencies, as an install records them.
        var ownPaths = ownAssets.RuntimeAssemblies.Select(a => Full(plugin.Directory, a)).ToList();
        var privatePaths = dependencies
            .Where(d => d.Classification == DependencyClass.CellPrivate)
            .SelectMany(d => assets[d.Id].RuntimeAssemblies.Select(a => Full(plugin.Directory, a)));
        CheckHostOwnedReferences(plugin.Id, ownPaths, dependencies.Where(d => d.Classification == DependencyClass.HostOwned).Select(d => d.Id).ToHashSet(StringComparer.OrdinalIgnoreCase), report);
        var wpfRelevant = WpfRelevance.FindRelevant(ownPaths.Concat(privatePaths).Select(Inspect).OfType<WpfAssemblyInfo>());
        var exposed = ExposedDependencyScanner.Scan(ownPaths, _host.IsHostOwnedAssembly);
        var state = new PluginState
        {
            Id = plugin.Id,
            Version = version,
            ContentHash = "",
            Source = plugin.Directory,
            Assemblies = WithRelevance(Records(ownAssets), wpfRelevant),
            Dependencies = dependencies.Select(d => d with { Assemblies = WithRelevance(d.Assemblies, wpfRelevant) }).ToList(),
            ExposedDependencies = [.. exposed.AssemblyNames.Order(StringComparer.OrdinalIgnoreCase)],
            Cell = manifest.Cell,
            ManifestSchemaVersion = manifest.SchemaVersion,
        };
        return new DevelopmentBuild(plugin.Source, plugin.Directory, state, manifest, assets);
    }

    /// <summary>
    /// The compatibility gate for host-owned packages the <c>deps.json</c> doesn't list (<c>Alcove.Sdk</c> keeps them out
    /// of the build output): the plugin's assemblies must not reference a higher version of a host-owned assembly than
    /// the host has, nor one the host doesn't have. An install checks the package's declared ranges instead.
    /// </summary>
    private void CheckHostOwnedReferences(string pluginId, IEnumerable<string> ownPaths, IReadOnlySet<string> checkedPackages, IssueSink report)
    {
        foreach (var path in ownPaths)
        {
            List<System.Reflection.AssemblyName> references;
            try
            {
                using var stream = File.OpenRead(path);
                using var pe = new PEReader(stream);
                if (!pe.HasMetadata)
                {
                    continue;
                }

                var md = pe.GetMetadataReader();
                references = md.AssemblyReferences.Select(h => md.GetAssemblyReference(h).GetAssemblyName()).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException)
            {
                continue;   // activation reports unreadable assemblies
            }

            foreach (var reference in references)
            {
                var name = reference.Name;
                if (name is null || _host.FrameworkAssemblies.ContainsKey(name) || !_host.IsHostOwnedAssembly(name))
                {
                    continue;
                }

                if (!_host.Assemblies.TryGetValue(name, out var hostAssembly))
                {
                    report(ValidationSeverity.Error, ValidationCodes.HostOwnedMissing,
                        $"The development plugin {pluginId} uses the host-owned assembly {name} {reference.Version}, which the host doesn't have.", pluginId);
                }
                else if (!checkedPackages.Contains(hostAssembly.Owner)
                    && reference.Version is { } needed && hostAssembly.AssemblyVersion is { } available && needed > available)
                {
                    report(ValidationSeverity.Error, ValidationCodes.HostOwnedConflict,
                        $"The development plugin {pluginId} was built against the host-owned assembly {name} {needed}, but the host has {available} (package {hostAssembly.Owner}): the plugin is incompatible with this host.",
                        pluginId);
                }
            }
        }
    }

    /// <summary>
    /// Classifies a dependency as an install would against this host; warns where the build output differs. A plugin the
    /// SDK recorded as a plugin dependency is one even if it's neither installed nor a development plugin: validation then
    /// reports it missing, as for an installed plugin, rather than the plugin loading its build output's copy.
    /// </summary>
    private DependencyClass Classify(
        string pluginId,
        DepsJsonReader.Library library,
        NuGetVersion version,
        IReadOnlySet<string> pluginIds,
        HashSet<string> pluginDependencies,
        HashSet<string> privateDependencies,
        IssueSink report)
    {
        if (pluginIds.Contains(library.Id) || pluginDependencies.Contains(library.Id))
        {
            return DependencyClass.Plugin;
        }

        if (_host.IsHostOwnedPackage(library.Id, version))
        {
            return DependencyClass.HostOwned;   // checked against the host by classification (HostOwnedConflict, HostOwnedMissing)
        }

        if (privateDependencies.Contains(library.Id))
        {
            return DependencyClass.CellPrivate;
        }

        if (_host.FindPackage(library.Id) is not { } hostPackage)
        {
            return DependencyClass.CellPrivate;
        }

        if (hostPackage.Version is { } hostVersion && hostVersion < version)
        {
            return DependencyClass.CellPrivate;   // an install keeps the newer version in the cell too
        }

        if (hostPackage.Version is { } newer && newer > version)
        {
            report(ValidationSeverity.Warning, ValidationCodes.DevelopmentDependencyDiffers,
                $"The development plugin {pluginId} was built against {library.Id} {version}, but it loads the host's {newer}, as an installed plugin would; its build output's copy isn't used.",
                pluginId);
        }

        return DependencyClass.HostShared;
    }

    /// <summary>
    /// A library's assets in the build output: RID-independent runtime and native assets are copied flat, satellites into
    /// their culture folder, and RID-specific assets (<c>runtimeTargets</c>) keep their <c>runtimes/&lt;rid&gt;/</c> path; the
    /// best RID for this host wins, as the .NET host picks them for an app. Host-owned assembly names are left out.
    /// </summary>
    private PackageAssets SelectAssets(string directory, DepsJsonReader.Library library, List<string> missing)
    {
        var runtime = library.Assets.Where(a => a.AssetType == "runtime" && a.RuntimeIdentifier is null).Select(a => FileName(a.Path)).ToList();
        var native = library.Assets.Where(a => a.AssetType == "native" && a.RuntimeIdentifier is null).Select(a => FileName(a.Path)).ToList();
        var satellites = library.Assets
            .Where(a => a.AssetType == "resources" && a.Culture is not null)
            .Select(a => new SatelliteAsset($"{a.Culture}/{FileName(a.Path)}", a.Culture!))
            .ToList();

        var specific = library.Assets.Where(a => a.RuntimeIdentifier is not null).ToList();
        if (specific.Count > 0 && !string.IsNullOrEmpty(_host.RuntimeIdentifier))
        {
            var preference = _assets.RuntimeGraph.ExpandRuntime(_host.RuntimeIdentifier).ToList();
            foreach (var assetType in new[] { "runtime", "native" })
            {
                var best = preference.FirstOrDefault(rid => specific.Any(a => a.AssetType == assetType && string.Equals(a.RuntimeIdentifier, rid, StringComparison.OrdinalIgnoreCase)));
                if (best is null)
                {
                    continue;
                }

                var paths = specific.Where(a => a.AssetType == assetType && string.Equals(a.RuntimeIdentifier, best, StringComparison.OrdinalIgnoreCase)).Select(a => a.Path).ToList();
                if (assetType == "runtime")
                {
                    runtime = paths;
                }
                else
                {
                    native = paths;
                }
            }
        }

        runtime = runtime.Where(r => !_host.IsHostOwnedAssembly(Path.GetFileNameWithoutExtension(r))).ToList();
        foreach (var path in runtime.Concat(native).Concat(satellites.Select(s => s.Path)))
        {
            if (!File.Exists(Full(directory, path)))
            {
                missing.Add(path);
            }
        }

        // RID-specific runtime assets keep their path, but only the file name names the assembly (and placeholders are dropped).
        return new PackageAssets(
            runtime.Where(r => !r.EndsWith("_._", StringComparison.Ordinal)).ToList(),
            native.Where(r => !r.EndsWith("_._", StringComparison.Ordinal)).ToList(),
            satellites);
    }

    private static IReadOnlyList<AssemblyRecord> Records(PackageAssets assets) =>
        assets.RuntimeAssemblies.Select(a => new AssemblyRecord { Name = Path.GetFileNameWithoutExtension(a), Path = a }).ToList();

    private static IReadOnlyList<AssemblyRecord> WithRelevance(IReadOnlyList<AssemblyRecord> assemblies, IReadOnlySet<string> relevant) =>
        assemblies.Select(a => a with { WpfRelevant = relevant.Contains(a.Name) }).ToList();

    private static DevelopmentBuild Empty(Identified plugin, NuGetVersion version) =>
        new(plugin.Source, plugin.Directory, new PluginState { Id = plugin.Id, Version = version, ContentHash = "", Source = plugin.Directory }, plugin.Manifest, new Dictionary<string, PackageAssets>());

    private static WpfAssemblyInfo? Inspect(string path)
    {
        try
        {
            return WpfAssemblyInspector.Inspect(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            return null;
        }
    }

    private static bool IsReference(DepsJsonReader.Library library) => library.Type.Equals("reference", StringComparison.OrdinalIgnoreCase);

    private static bool IsProject(DepsJsonReader.Library library) => library.Type.Equals("project", StringComparison.OrdinalIgnoreCase);

    private static bool IsFile(string path, string fileName) => string.Equals(FileName(path), fileName, StringComparison.OrdinalIgnoreCase);

    private static string FileName(string path) => path.Split('/')[^1];

    private static string Full(string directory, string relative) => Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar));

    private static string BaseName(string path, string suffix)
    {
        var name = Path.GetFileName(path);
        return name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? name[..^suffix.Length] : name;
    }

    private sealed record Identified(
        DevelopmentPlugin Source, string Directory, string Where, string Id, PluginManifest? Manifest, string? ManifestError, string? DepsPath, int DepsFileCount);
}

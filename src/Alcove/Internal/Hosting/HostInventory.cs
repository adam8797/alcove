using System.Reflection;
using System.Runtime.Versioning;
using NuGet.Frameworks;
using NuGet.Versioning;

namespace Alcove.Internal.Hosting;

/// <summary>A library listed in the host's <c>deps.json</c>: a package, or a project (including the app itself).</summary>
/// <param name="Id">The package or project ID.</param>
/// <param name="Version">The version.</param>
/// <param name="Type"><c>package</c>, <c>project</c>, <c>reference</c>, ...</param>
/// <param name="Sha512">The package's <c>contentHash</c> as recorded in <c>deps.json</c>, if any.</param>
/// <param name="Dependencies">IDs of the libraries this one depends on.</param>
/// <param name="Assemblies">Its runtime assemblies.</param>
internal sealed record HostPackage(
    string Id,
    NuGetVersion? Version,
    string Type,
    string? Sha512,
    IReadOnlyList<string> Dependencies,
    IReadOnlyList<HostAssembly> Assemblies)
{
    /// <summary>Whether this is a NuGet package (as opposed to a project).</summary>
    public bool IsPackage => string.Equals(Type, "package", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A managed assembly the host's default context provides.</summary>
/// <param name="Name">The simple name.</param>
/// <param name="AssemblyVersion">The <c>AssemblyVersion</c>, which often differs from the package version.</param>
/// <param name="Owner">The package or project ID, or the shared framework's name.</param>
internal sealed record HostAssembly(string Name, Version? AssemblyVersion, string Owner);

/// <summary>A shared framework the process actually runs on (the resolved version, not the requested one).</summary>
internal sealed record SharedFramework(string Name, NuGetVersion Version, string Source);

/// <summary>
/// What the host provides, read from its <c>deps.json</c> files (<c>APP_CONTEXT_DEPS_FILES</c>) and its entry
/// assembly's framework attributes (architecture §4). Immutable.
/// </summary>
internal sealed class HostInventory : IHostAssemblies
{
    /// <summary>Packages that are always host-owned, whatever the app lists (with their dependencies).</summary>
    public static readonly IReadOnlyList<string> AlwaysHostOwned =
        ["Alcove.Abstractions", "NuGet.Versioning", "Microsoft.Extensions.Logging.Abstractions"];

    private readonly Dictionary<string, HostPackage> _packages;
    private readonly Dictionary<string, HostAssembly> _assemblies;
    private readonly Dictionary<string, HostAssembly> _frameworkAssemblies;
    private readonly HashSet<string> _hostOwnedPackages;
    private readonly int _frameworkMajor;

    internal HostInventory(
        NuGetFramework framework,
        string runtimeIdentifier,
        string appDepsFile,
        IReadOnlyList<SharedFramework> sharedFrameworks,
        IEnumerable<HostPackage> packages,
        IEnumerable<HostAssembly> frameworkAssemblies,
        IEnumerable<string> hostOwnedPackages)
    {
        Framework = framework;
        RuntimeIdentifier = runtimeIdentifier;
        AppDepsFile = appDepsFile;
        SharedFrameworks = sharedFrameworks;

        _packages = new(StringComparer.OrdinalIgnoreCase);
        _assemblies = new(StringComparer.OrdinalIgnoreCase);
        foreach (var package in packages)
        {
            _packages[package.Id] = package;
            foreach (var assembly in package.Assemblies)
            {
                _assemblies[assembly.Name] = assembly;
            }
        }

        _frameworkAssemblies = new(StringComparer.OrdinalIgnoreCase);
        foreach (var assembly in frameworkAssemblies)
        {
            _frameworkAssemblies.TryAdd(assembly.Name, assembly);
            _assemblies.Remove(assembly.Name);
        }

        var netCore = sharedFrameworks.FirstOrDefault(f => string.Equals(f.Name, "Microsoft.NETCore.App", StringComparison.OrdinalIgnoreCase));
        _frameworkMajor = netCore?.Version.Major ?? (sharedFrameworks.Count == 0 ? 0 : sharedFrameworks.Max(f => f.Version.Major));
        _hostOwnedPackages = CloseOverDependencies(AlwaysHostOwned.Concat(hostOwnedPackages));
    }

    /// <summary>The host's NuGet framework, from the entry assembly's attributes, e.g. <c>net10.0-windows7.0</c>.</summary>
    public NuGetFramework Framework { get; }

    /// <summary>The runtime identifier for RID-specific assets.</summary>
    public string RuntimeIdentifier { get; }

    /// <summary>The app's <c>deps.json</c>.</summary>
    public string AppDepsFile { get; }

    /// <summary>The shared frameworks the process runs on.</summary>
    public IReadOnlyList<SharedFramework> SharedFrameworks { get; }

    /// <summary>Packages and projects of the app (not the shared frameworks), by ID.</summary>
    public IReadOnlyDictionary<string, HostPackage> Packages => _packages;

    /// <summary>The app's own assemblies (not the shared frameworks'), by simple name.</summary>
    public IReadOnlyDictionary<string, HostAssembly> Assemblies => _assemblies;

    /// <summary>The shared frameworks' assemblies, by simple name.</summary>
    public IReadOnlyDictionary<string, HostAssembly> FrameworkAssemblies => _frameworkAssemblies;

    /// <summary>The host-owned package IDs, closed over the host's dependency graph.</summary>
    public IReadOnlySet<string> HostOwnedPackages => _hostOwnedPackages;

    /// <summary>Reads the inventory of the running process.</summary>
    /// <exception cref="UnsupportedHostException">The host is a single-file app or has no <c>deps.json</c>.</exception>
    public static HostInventory FromCurrentProcess(IEnumerable<string> hostOwnedPackages) =>
        Create(HostEnvironment.Current, hostOwnedPackages);

    /// <summary>Reads the inventory described by <paramref name="environment"/>.</summary>
    /// <exception cref="UnsupportedHostException">There is no inventory to read (single-file host, missing <c>deps.json</c>).</exception>
    public static HostInventory Create(HostEnvironment environment, IEnumerable<string> hostOwnedPackages)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(hostOwnedPackages);

        if (environment.EntryAssembly is { } entry && string.IsNullOrEmpty(entry.Location))
        {
            throw new UnsupportedHostException(
                $"The host '{entry.GetName().Name}' is a single-file app. Single-file hosts expose no deps.json, so Alcove can't tell which assemblies the host provides. Publish the host without PublishSingleFile.");
        }

        var depsFiles = (environment.DepsFiles ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var appDepsFiles = depsFiles.Where(f => !DepsJsonReader.IsFrameworkDepsFile(f)).ToList();
        if (appDepsFiles.Count == 0 || !File.Exists(appDepsFiles[0]))
        {
            throw new UnsupportedHostException(
                appDepsFiles.Count == 0
                    ? "The host has no deps.json (APP_CONTEXT_DEPS_FILES is empty), which is the case for single-file apps. Alcove needs the host's deps.json to know which assemblies it provides."
                    : $"The host's deps.json '{appDepsFiles[0]}' doesn't exist. Alcove needs it to know which assemblies the host provides; ship it next to the app.");
        }

        var packages = new List<HostPackage>();
        var frameworks = new List<SharedFramework>();
        var frameworkAssemblies = new List<HostAssembly>();
        NuGetFramework? depsFramework = null;
        foreach (var appDeps in appDepsFiles.Where(File.Exists))
        {
            var app = DepsJsonReader.ReadAppDeps(appDeps);
            depsFramework ??= app.Framework;
            packages.AddRange(app.Packages);
            frameworks.AddRange(app.RuntimePacks);
            frameworkAssemblies.AddRange(app.RuntimePackAssemblies);
        }

        foreach (var frameworkDeps in depsFiles.Where(DepsJsonReader.IsFrameworkDepsFile))
        {
            var (framework, assemblies) = DepsJsonReader.ReadFrameworkDeps(frameworkDeps);
            frameworks.Add(framework);
            frameworkAssemblies.AddRange(assemblies);
        }

        var hostFramework = environment.EntryAssembly is { } entryAssembly
            ? FrameworkFromAttributes(entryAssembly) ?? depsFramework
            : depsFramework;

        return new HostInventory(
            hostFramework ?? FrameworkConstants.CommonFrameworks.Net10_0,
            environment.RuntimeIdentifier,
            appDepsFiles[0],
            frameworks,
            packages,
            frameworkAssemblies,
            hostOwnedPackages);
    }

    /// <summary>
    /// The host framework from an assembly's <see cref="TargetFrameworkAttribute"/> and
    /// <see cref="TargetPlatformAttribute"/>. <c>deps.json</c> drops the platform version
    /// (<c>net10.0-windows7.0</c> becomes <c>net10.0-windows</c>), which would select the wrong assets.
    /// </summary>
    public static NuGetFramework? FrameworkFromAttributes(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var targetFramework = assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName;
        if (string.IsNullOrEmpty(targetFramework))
        {
            return null;
        }

        return FrameworkFromAttributes(targetFramework, assembly.GetCustomAttribute<TargetPlatformAttribute>()?.PlatformName);
    }

    /// <summary>Combines a target framework name (<c>.NETCoreApp,Version=v10.0</c>) with a target platform (<c>Windows7.0</c>).</summary>
    public static NuGetFramework FrameworkFromAttributes(string targetFramework, string? targetPlatform)
    {
        var framework = NuGetFramework.ParseFrameworkName(targetFramework, DefaultFrameworkNameProvider.Instance);
        if (string.IsNullOrEmpty(targetPlatform))
        {
            return framework;
        }

        var digit = targetPlatform.AsSpan().IndexOfAnyInRange('0', '9');
        var (name, version) = digit < 0 ? (targetPlatform, "0.0") : (targetPlatform[..digit], targetPlatform[digit..]);
        return new NuGetFramework(framework.Framework, framework.Version, name.ToLowerInvariant(), Version.Parse(version));
    }

    /// <summary>The host's package or project with this ID, or <see langword="null"/>.</summary>
    public HostPackage? FindPackage(string id) => _packages.GetValueOrDefault(id);

    /// <summary>
    /// Whether a package is provided by the shared framework, so the SDK would prune it: its ID equals a
    /// framework assembly name and its major version is at most the framework's. Such packages are skipped,
    /// not version-checked.
    /// </summary>
    public bool IsFrameworkProvided(string packageId, NuGetVersion? version = null)
    {
        if (version is not null && version.Major > _frameworkMajor)
        {
            return false;
        }

        if (_frameworkAssemblies.ContainsKey(packageId))
        {
            return true;
        }

        // Old RID-split implementation packages, e.g. runtime.unix.System.IO.FileSystem.
        return packageId.StartsWith("runtime.", StringComparison.OrdinalIgnoreCase)
            && _frameworkAssemblies.Keys.Any(a => packageId.EndsWith("." + a, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Whether a package is host-owned: listed (or a dependency of one listed), or framework-provided.</summary>
    public bool IsHostOwnedPackage(string packageId, NuGetVersion? version = null) =>
        _hostOwnedPackages.Contains(packageId) || IsFrameworkProvided(packageId, version);

    /// <summary>
    /// Whether an assembly is host-owned: a shared framework assembly, or an assembly of a host-owned package.
    /// A host-owned package the host doesn't carry counts by its ID (the usual assembly name), so a plugin
    /// needing it fails instead of loading a private copy.
    /// </summary>
    public bool IsHostOwnedAssembly(string assemblyName) =>
        _frameworkAssemblies.ContainsKey(assemblyName)
        || (_assemblies.TryGetValue(assemblyName, out var assembly) && _hostOwnedPackages.Contains(assembly.Owner))
        || _hostOwnedPackages.Contains(assemblyName);

    /// <summary>Whether the host's <c>deps.json</c> provides an assembly that isn't host-owned.</summary>
    public bool IsHostSharedAssembly(string assemblyName) =>
        _assemblies.ContainsKey(assemblyName) && !IsHostOwnedAssembly(assemblyName);

    private HashSet<string> CloseOverDependencies(IEnumerable<string> seeds)
    {
        var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<string>(seeds);
        while (pending.TryDequeue(out var id))
        {
            if (!owned.Add(id) || !_packages.TryGetValue(id, out var package))
            {
                continue;
            }

            foreach (var dependency in package.Dependencies)
            {
                pending.Enqueue(dependency);
            }
        }

        return owned;
    }
}

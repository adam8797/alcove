using System.Reflection;
using System.Runtime.InteropServices;

namespace Alcove.Tests.TestSupport;

/// <summary>
/// Alcove.Wpf (net10.0-windows) read with a <see cref="MetadataLoadContext"/>: it can't be loaded into this net10.0
/// process (no WPF on Linux), but its metadata can be read against the WPF reference assemblies (the
/// Microsoft.WindowsDesktop.App targeting pack the build already uses). For the public API snapshot and the event ID checks.
/// </summary>
internal static class WpfAddOnMetadata
{
    private static readonly Lazy<(MetadataLoadContext Context, Assembly Assembly)> s_loaded = new(Load);

    /// <summary>Alcove.Wpf, in a metadata-only context kept for the test run.</summary>
    public static Assembly Assembly => s_loaded.Value.Assembly;

    private static (MetadataLoadContext, Assembly) Load()
    {
        var path = Metadata("AlcoveWpfAssembly");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"'{path}' wasn't built. Build the solution first.", path);
        }

        // By simple name; the first wins: the add-on and its dependencies, the WPF reference assemblies, then the runtime.
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void AddAll(string directory)
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.dll"))
            {
                files.TryAdd(Path.GetFileNameWithoutExtension(file), file);
            }
        }

        files[Path.GetFileNameWithoutExtension(path)] = path;
        AddAll(AppContext.BaseDirectory);
        AddAll(FindWpfReferenceAssemblies());
        AddAll(RuntimeEnvironment.GetRuntimeDirectory());

        var context = new MetadataLoadContext(new PathAssemblyResolver(files.Values), typeof(object).Assembly.GetName().Name);
        return (context, context.LoadFromAssemblyPath(path));
    }

    /// <summary>
    /// The highest <c>Microsoft.WindowsDesktop.App.Ref</c> for this runtime's major version: in the SDK's <c>packs</c>
    /// folder (Windows SDK installs) or the NuGet packages folder (where <c>EnableWindowsTargeting</c> downloads it).
    /// </summary>
    private static string FindWpfReferenceAssemblies()
    {
        var major = Environment.Version.Major;
        var roots = new[]
        {
            Path.Combine(Metadata("NetCoreTargetingPackRoot"), "Microsoft.WindowsDesktop.App.Ref"),
            Path.Combine(Metadata("NuGetPackageRoot"), "microsoft.windowsdesktop.app.ref"),
        };
        var candidates = roots
            .Where(Directory.Exists)
            .SelectMany(Directory.EnumerateDirectories)
            .Select(d => (Directory: Path.Combine(d, "ref", $"net{major}.0"), Version: Version.TryParse(Path.GetFileName(d).Split('-')[0], out var v) ? v : null))
            .Where(c => c.Version?.Major == major && File.Exists(Path.Combine(c.Directory, "PresentationFramework.dll")))
            .OrderByDescending(c => c.Version)
            .ToList();
        return candidates.Count > 0
            ? candidates[0].Directory
            : throw new DirectoryNotFoundException($"No Microsoft.WindowsDesktop.App.Ref {major}.x found under {string.Join(" or ", roots)}. Restore the solution first.");
    }

    private static string Metadata(string key) =>
        typeof(WpfAddOnMetadata).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().SingleOrDefault(a => a.Key == key)?.Value
        ?? throw new InvalidOperationException($"Assembly metadata '{key}' is missing.");
}

using Alcove.Packaging;
using NuGet.Versioning;

namespace Alcove.Cells;

/// <summary>A cell-private assembly: a file in the plugin root.</summary>
internal sealed record PrivateAssembly(string Name, string Path, string PackageId, NuGetVersion PackageVersion)
{
    /// <summary>Whether it's WPF-relevant; identical WPF-relevant private assemblies are loaded once for all cells.</summary>
    public bool WpfRelevant { get; init; }
}

/// <summary>A satellite (localised resource) assembly of a cell-private package.</summary>
internal sealed record SatelliteAssembly(string Name, string Culture, string Path, string PackageId, NuGetVersion PackageVersion);

/// <summary>A native library of a cell-private package, from <c>runtimes/&lt;rid&gt;/native/</c>.</summary>
internal sealed record NativeLibraryAsset(string FileName, string Path, string PackageId, NuGetVersion PackageVersion);

/// <summary>
/// The planned answers of a cell for every assembly its plugins need (architecture §5). Names not in the
/// plan fall back to the host inventory (host-owned, host-shared) or are unknown.
/// </summary>
internal sealed class CellPlan
{
    /// <summary>IDs of the plugins in the cell.</summary>
    public ISet<string> Plugins { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Host-owned assembly names (step 1), beyond what the host inventory says.</summary>
    public ISet<string> HostOwned { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Plugin-provided assemblies (step 2): assembly name → providing cell name.</summary>
    public IDictionary<string, string> Provided { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Cell-private assemblies (step 3), by assembly name; includes the cell's own plugin assemblies.</summary>
    public IDictionary<string, PrivateAssembly> Private { get; init; } = new Dictionary<string, PrivateAssembly>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Host-shared assembly names (step 4), beyond what the host inventory says.</summary>
    public ISet<string> HostShared { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Satellite assemblies of cell-private packages, by <c>name/culture</c> (see <see cref="SatelliteKey"/>).</summary>
    public IDictionary<string, SatelliteAssembly> Satellites { get; init; } = new Dictionary<string, SatelliteAssembly>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Native libraries of cell-private packages, by file name.</summary>
    public IDictionary<string, NativeLibraryAsset> NativeLibraries { get; init; } = new Dictionary<string, NativeLibraryAsset>(NativeFileNameComparer);

    /// <summary>Compares native file names as this OS's loader does.</summary>
    public static StringComparer NativeFileNameComparer => NativeLibraryNames.FileNameComparer;

    /// <summary>The key of a satellite assembly: <c>Foo.resources/de</c>.</summary>
    public static string SatelliteKey(string name, string culture) => $"{name}/{culture}";

    /// <summary>The planned answer for every assembly name in the plan.</summary>
    public Dictionary<string, CellAnswer> GetPlannedAssemblyAnswers()
    {
        var answers = new Dictionary<string, CellAnswer>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in HostShared)
        {
            answers[name] = CellAnswer.Host(AnswerReason.HostShared);
        }

        foreach (var (key, satellite) in Satellites)
        {
            answers[key] = PrivateAnswer(satellite.Path, satellite.PackageId, satellite.PackageVersion, AnswerReason.CellPrivate);
        }

        foreach (var (name, assembly) in Private)
        {
            answers[name] = PrivateAnswer(assembly.Path, assembly.PackageId, assembly.PackageVersion, AnswerReason.CellPrivate);
        }

        foreach (var (name, cell) in Provided)
        {
            answers[name] = new CellAnswer(AnswerSource.Provider, AnswerReason.PluginProvided) { ProviderCell = cell };
        }

        foreach (var name in HostOwned)
        {
            answers[name] = CellAnswer.Host(AnswerReason.HostOwned);
        }

        return answers;
    }

    /// <summary>The planned answer for every native library in the plan, by file name.</summary>
    public Dictionary<string, CellAnswer> GetPlannedNativeAnswers()
    {
        var answers = new Dictionary<string, CellAnswer>(NativeFileNameComparer);
        foreach (var (fileName, native) in NativeLibraries)
        {
            answers[fileName] = PrivateAnswer(native.Path, native.PackageId, native.PackageVersion, AnswerReason.CellPrivate);
        }

        return answers;
    }

    internal static CellAnswer PrivateAnswer(string path, string packageId, NuGetVersion version, AnswerReason reason) =>
        new(AnswerSource.Private, reason) { Path = path, PackageId = packageId, PackageVersion = version };
}

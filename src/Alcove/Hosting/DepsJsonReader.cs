using System.Text.Json;
using NuGet.Frameworks;
using NuGet.Versioning;

namespace Alcove.Hosting;

/// <summary>Reads <c>deps.json</c> files with System.Text.Json only (no Microsoft.Extensions.DependencyModel).</summary>
internal static class DepsJsonReader
{
    /// <summary>What an app's <c>deps.json</c> says.</summary>
    public sealed record AppDeps(
        NuGetFramework? Framework,
        string? RuntimeIdentifier,
        IReadOnlyList<HostPackage> Packages,
        IReadOnlyList<SharedFramework> RuntimePacks,
        IReadOnlyList<HostAssembly> RuntimePackAssemblies);

    /// <summary>
    /// Whether a path is a shared framework's deps file: <c>.../shared/&lt;Name&gt;/&lt;version&gt;/&lt;Name&gt;.deps.json</c>.
    /// </summary>
    public static bool IsFrameworkDepsFile(string path)
    {
        var versionDir = Path.GetDirectoryName(path);
        var nameDir = versionDir is null ? null : Path.GetDirectoryName(versionDir);
        var sharedDir = nameDir is null ? null : Path.GetDirectoryName(nameDir);
        return sharedDir is not null
            && string.Equals(Path.GetFileName(sharedDir), "shared", StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetFileName(path), Path.GetFileName(nameDir) + ".deps.json", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Reads an app's <c>deps.json</c>: packages and projects with their dependencies and runtime assemblies.</summary>
    public static AppDeps ReadAppDeps(string path)
    {
        using var document = Parse(path);
        var root = document.RootElement;
        var (targetName, target) = GetTarget(root, path);
        var slash = targetName.IndexOf('/', StringComparison.Ordinal);
        var frameworkName = slash < 0 ? targetName : targetName[..slash];
        var framework = string.IsNullOrEmpty(frameworkName) ? null : NuGetFramework.ParseFrameworkName(frameworkName, DefaultFrameworkNameProvider.Instance);
        var rid = slash < 0 ? null : targetName[(slash + 1)..];
        var libraries = root.TryGetProperty("libraries", out var l) && l.ValueKind == JsonValueKind.Object ? l : default;

        var packages = new List<HostPackage>();
        var runtimePacks = new List<SharedFramework>();
        var runtimePackAssemblies = new List<HostAssembly>();
        foreach (var library in target.EnumerateObject())
        {
            var (id, versionText) = SplitKey(library.Name);
            NuGetVersion.TryParse(versionText, out var version);
            var info = libraries.ValueKind == JsonValueKind.Object && libraries.TryGetProperty(library.Name, out var i) && i.ValueKind == JsonValueKind.Object ? i : default;
            var type = GetString(info, "type") ?? "unknown";
            var sha512 = GetString(info, "sha512");

            var dependencies = new List<string>();
            if (library.Value.TryGetProperty("dependencies", out var deps) && deps.ValueKind == JsonValueKind.Object)
            {
                dependencies.AddRange(deps.EnumerateObject().Select(d => d.Name));
            }

            if (string.Equals(type, "runtimepack", StringComparison.OrdinalIgnoreCase))
            {
                // Self-contained app: the shared framework is carried as runtimepack.<Framework>.Runtime.<rid>/<version>.
                var start = "runtimepack.".Length;
                var end = id.IndexOf(".Runtime.", StringComparison.OrdinalIgnoreCase);
                var frameworkId = id.StartsWith("runtimepack.", StringComparison.OrdinalIgnoreCase) && end > start ? id[start..end] : id;
                if (version is not null)
                {
                    runtimePacks.Add(new SharedFramework(frameworkId, version, path));
                }

                runtimePackAssemblies.AddRange(ReadRuntimeAssemblies(library.Value, frameworkId));
                continue;
            }

            packages.Add(new HostPackage(id, version, type, sha512, dependencies, ReadRuntimeAssemblies(library.Value, id)));
        }

        return new AppDeps(framework, rid, packages, runtimePacks, runtimePackAssemblies);
    }

    /// <summary>Reads a shared framework's <c>deps.json</c>; the name and resolved version come from its path.</summary>
    public static (SharedFramework Framework, IReadOnlyList<HostAssembly> Assemblies) ReadFrameworkDeps(string path)
    {
        var versionDir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileName(Path.GetDirectoryName(versionDir)!);
        var versionText = Path.GetFileName(versionDir);
        if (!NuGetVersion.TryParse(versionText, out var version))
        {
            throw new InvalidDataException($"The shared framework deps file '{path}' isn't in a version folder ('{versionText}').");
        }

        var assemblies = new List<HostAssembly>();
        if (File.Exists(path))
        {
            using var document = Parse(path);
            var (_, target) = GetTarget(document.RootElement, path);
            foreach (var library in target.EnumerateObject())
            {
                assemblies.AddRange(ReadRuntimeAssemblies(library.Value, name));
            }
        }

        return (new SharedFramework(name, version, path), assemblies);
    }

    private static List<HostAssembly> ReadRuntimeAssemblies(JsonElement library, string owner)
    {
        var assemblies = new List<HostAssembly>();
        if (!library.TryGetProperty("runtime", out var runtime) || runtime.ValueKind != JsonValueKind.Object)
        {
            return assemblies;
        }

        foreach (var asset in runtime.EnumerateObject())
        {
            // RID-specific assets of a portable app (runtimeTargets) are deliberately ignored: the runtime picks
            // one per RID at startup, and for the inventory the package ID and version are what matter.
            var name = Path.GetFileNameWithoutExtension(asset.Name.Replace('\\', '/').Split('/')[^1]);
            Version.TryParse(GetString(asset.Value, "assemblyVersion"), out var assemblyVersion);
            assemblies.Add(new HostAssembly(name, assemblyVersion, owner));
        }

        return assemblies;
    }

    private static JsonDocument Parse(string path)
    {
        try
        {
            return JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"The deps file '{path}' is malformed: {ex.Message}", ex);
        }
    }

    private static (string Name, JsonElement Target) GetTarget(JsonElement root, string path)
    {
        if (root.TryGetProperty("runtimeTarget", out var runtimeTarget)
            && GetString(runtimeTarget, "name") is { } name
            && root.TryGetProperty("targets", out var targets)
            && targets.TryGetProperty(name, out var target)
            && target.ValueKind == JsonValueKind.Object)
        {
            return (name, target);
        }

        throw new InvalidDataException($"The deps file '{path}' has no runtime target.");
    }

    private static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static (string Id, string Version) SplitKey(string key)
    {
        var slash = key.LastIndexOf('/');
        return slash < 0 ? (key, "") : (key[..slash], key[(slash + 1)..]);
    }
}

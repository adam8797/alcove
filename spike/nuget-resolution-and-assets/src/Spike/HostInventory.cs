using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;

namespace Spike;

/// <summary>A package the host app carries in its own deps.json.</summary>
public sealed record HostPackage(string Id, string Version, string? Sha512, IReadOnlyDictionary<string, string> Dependencies, IReadOnlyList<HostAssembly> Assemblies, string Type);

/// <summary>A managed assembly visible to the default load context.</summary>
public sealed record HostAssembly(string Name, string? AssemblyVersion, string? FileVersion, string Owner, string RelativePath);

/// <summary>A shared framework the running process actually uses (resolved version, not the requested one).</summary>
public sealed record SharedFramework(string Name, string Version, string DepsFile);

/// <summary>
/// Reads the host's own deps.json and the shared frameworks' deps.json files with System.Text.Json only
/// (no Microsoft.Extensions.DependencyModel).
/// </summary>
public sealed class HostInventory
{
    public string AppDepsFile { get; private init; } = "";
    public string TargetFramework { get; private set; } = "";      // e.g. ".NETCoreApp,Version=v10.0"
    public string? RuntimeTargetRid { get; private set; }         // set for RID-specific (self-contained / -r) builds
    public List<SharedFramework> Frameworks { get; } = new();
    public Dictionary<string, HostPackage> Packages { get; } = new(StringComparer.OrdinalIgnoreCase);   // id -> package (type=package)
    public Dictionary<string, HostPackage> Projects { get; } = new(StringComparer.OrdinalIgnoreCase);   // project references incl. the app itself
    public Dictionary<string, HostAssembly> Assemblies { get; } = new(StringComparer.OrdinalIgnoreCase); // assembly simple name -> owner
    public Dictionary<string, HostAssembly> FrameworkAssemblies { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string[] TrustedPlatformAssemblies { get; private init; } = [];
    public bool AppDepsMissing { get; private set; }

    public static HostInventory FromCurrentProcess()
    {
        // "APP_CONTEXT_DEPS_FILES": ';'-separated list: the app's deps.json first, then one per shared framework.
        // NB: always ';' (even on Linux), unlike TRUSTED_PLATFORM_ASSEMBLIES which uses Path.PathSeparator (':' on Unix).
        var depsFiles = ((AppContext.GetData("APP_CONTEXT_DEPS_FILES") as string) ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries);
        var tpa = ((AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string) ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        string appDeps = depsFiles.FirstOrDefault(f => !IsFrameworkDepsFile(f))
            ?? Path.Combine(AppContext.BaseDirectory, Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "app") + ".deps.json");

        var inv = new HostInventory { AppDepsFile = appDeps, TrustedPlatformAssemblies = tpa };
        if (File.Exists(appDeps)) inv.ReadAppDeps(appDeps);
        else inv.AppDepsMissing = true;   // single-file: deps.json is embedded in the bundle, not on disk

        foreach (var fx in depsFiles.Where(IsFrameworkDepsFile))
        {
            // .../shared/<FrameworkName>/<version>/<FrameworkName>.deps.json
            var dir = Path.GetDirectoryName(fx)!;
            var version = Path.GetFileName(dir);
            var name = Path.GetFileName(Path.GetDirectoryName(dir)!);
            inv.Frameworks.Add(new SharedFramework(name, version, fx));
            inv.ReadFrameworkDeps(fx, name);
        }
        return inv;
    }

    public static HostInventory FromFiles(string appDepsFile, IEnumerable<string> frameworkDepsFiles)
    {
        var inv = new HostInventory { AppDepsFile = appDepsFile };
        inv.ReadAppDeps(appDepsFile);
        foreach (var fx in frameworkDepsFiles)
        {
            var dir = Path.GetDirectoryName(fx)!;
            var name = Path.GetFileName(Path.GetDirectoryName(dir)!);
            inv.Frameworks.Add(new SharedFramework(name, Path.GetFileName(dir), fx));
            inv.ReadFrameworkDeps(fx, name);
        }
        return inv;
    }

    static bool IsFrameworkDepsFile(string path)
    {
        // Framework deps files live in <dotnet>/shared/<name>/<version>/ and are named after the framework.
        var dir = Path.GetDirectoryName(path);
        if (dir is null) return false;
        var parent = Path.GetDirectoryName(dir);
        var grand = parent is null ? null : Path.GetDirectoryName(parent);
        return grand is not null && Path.GetFileName(grand) == "shared"
            && Path.GetFileName(path).Equals(Path.GetFileName(parent) + ".deps.json", StringComparison.OrdinalIgnoreCase);
    }

    void ReadAppDeps(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = doc.RootElement;
        var targetName = root.GetProperty("runtimeTarget").GetProperty("name").GetString()!;   // ".NETCoreApp,Version=v10.0" or ".../linux-x64"
        var slash = targetName.IndexOf('/');
        TargetFramework = slash < 0 ? targetName : targetName[..slash];
        RuntimeTargetRid = slash < 0 ? null : targetName[(slash + 1)..];

        var libraries = root.GetProperty("libraries");
        foreach (var lib in root.GetProperty("targets").GetProperty(targetName).EnumerateObject())
        {
            var (id, version) = SplitKey(lib.Name);
            var libInfo = libraries.TryGetProperty(lib.Name, out var li) ? li : default;
            var type = libInfo.ValueKind == JsonValueKind.Object && libInfo.TryGetProperty("type", out var t) ? t.GetString()! : "unknown";
            var sha = libInfo.ValueKind == JsonValueKind.Object && libInfo.TryGetProperty("sha512", out var s) ? s.GetString() : null;

            var deps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (lib.Value.TryGetProperty("dependencies", out var d))
                foreach (var dep in d.EnumerateObject()) deps[dep.Name] = dep.Value.GetString()!;

            var asms = new List<HostAssembly>();
            if (lib.Value.TryGetProperty("runtime", out var rt))
                foreach (var a in rt.EnumerateObject())
                {
                    var asm = new HostAssembly(Path.GetFileNameWithoutExtension(a.Name),
                        a.Value.TryGetProperty("assemblyVersion", out var av) ? av.GetString() : null,
                        a.Value.TryGetProperty("fileVersion", out var fv) ? fv.GetString() : null,
                        $"{id}/{version}", a.Name);
                    asms.Add(asm);
                    Assemblies[asm.Name] = asm;
                }
            // runtimeTargets (RID-specific assets of a portable app) are deliberately not added to Assemblies here:
            // the runtime picks one per RID at startup; for the inventory the package id/version is what matters.

            if (type == "runtimepack")
            {
                // self-contained app: the shared framework is carried as "runtimepack.<Fx>.Runtime.<rid>/<version>"
                var fxName = id["runtimepack.".Length..id.IndexOf(".Runtime.", StringComparison.Ordinal)];
                Frameworks.Add(new SharedFramework(fxName, version, path + " (runtimepack, self-contained)"));
                foreach (var a in asms) { Assemblies.Remove(a.Name); FrameworkAssemblies[a.Name] = a with { Owner = fxName }; }
                continue;
            }
            var pkg = new HostPackage(id, version, sha, deps, asms, type);
            if (type == "package") Packages[id] = pkg; else Projects[id] = pkg;
        }
    }

    void ReadFrameworkDeps(string path, string fxName)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = doc.RootElement;
        var targetName = root.GetProperty("runtimeTarget").GetProperty("name").GetString()!;
        foreach (var lib in root.GetProperty("targets").GetProperty(targetName).EnumerateObject())
            if (lib.Value.TryGetProperty("runtime", out var rt))
                foreach (var a in rt.EnumerateObject())
                {
                    var asm = new HostAssembly(Path.GetFileNameWithoutExtension(a.Name),
                        a.Value.TryGetProperty("assemblyVersion", out var av) ? av.GetString() : null,
                        a.Value.TryGetProperty("fileVersion", out var fv) ? fv.GetString() : null,
                        fxName, a.Name);
                    FrameworkAssemblies[asm.Name] = asm;
                }
    }

    static (string id, string version) SplitKey(string key)
    {
        var i = key.LastIndexOf('/');
        return (key[..i], key[(i + 1)..]);
    }

    /// <summary>
    /// The host's NuGet framework. deps.json says only ".NETCoreApp,Version=v10.0" even for net10.0-windows apps;
    /// the platform part comes from the entry assembly's [TargetPlatform("Windows7.0")].
    /// </summary>
    public static NuGet.Frameworks.NuGetFramework HostFramework(System.Reflection.Assembly? entry = null)
    {
        entry ??= System.Reflection.Assembly.GetEntryAssembly()!;
        var tfa = entry.GetCustomAttributes(typeof(System.Runtime.Versioning.TargetFrameworkAttribute), false)
            .Cast<System.Runtime.Versioning.TargetFrameworkAttribute>().Single().FrameworkName;
        var tpa = entry.GetCustomAttributes(typeof(System.Runtime.Versioning.TargetPlatformAttribute), false)
            .Cast<System.Runtime.Versioning.TargetPlatformAttribute>().SingleOrDefault()?.PlatformName;
        return FromAttributes(tfa, tpa);
    }

    public static NuGet.Frameworks.NuGetFramework FromAttributes(string targetFramework, string? targetPlatform)
    {
        var fw = NuGet.Frameworks.NuGetFramework.ParseFrameworkName(targetFramework, NuGet.Frameworks.DefaultFrameworkNameProvider.Instance);
        if (string.IsNullOrEmpty(targetPlatform)) return fw;
        var i = targetPlatform.IndexOfAny("0123456789".ToCharArray());
        var (name, ver) = i < 0 ? (targetPlatform, "0.0") : (targetPlatform[..i], targetPlatform[i..]);
        return new NuGet.Frameworks.NuGetFramework(fw.Framework, fw.Version, name.ToLowerInvariant(), Version.Parse(ver));
    }

    public void Dump(TextWriter w, bool verbose = false)
    {
        w.WriteLine($"Host NuGet framework: {HostFramework().GetShortFolderName()}   (FromAttributes(\".NETCoreApp,Version=v10.0\", \"Windows7.0\") = {FromAttributes(".NETCoreApp,Version=v10.0", "Windows7.0").GetShortFolderName()})");
        w.WriteLine($"App deps file      : {AppDepsFile}{(AppDepsMissing ? "   ** NOT ON DISK **" : "")}");
        w.WriteLine($"Target framework   : {TargetFramework}   runtimeTarget RID: {RuntimeTargetRid ?? "(portable)"}");
        w.WriteLine($"RuntimeInformation : {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}, RID {System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier}");
        w.WriteLine($"Shared frameworks  : {Frameworks.Count}");
        foreach (var f in Frameworks) w.WriteLine($"    {f.Name} {f.Version}   ({f.DepsFile})");
        w.WriteLine($"Framework assemblies: {FrameworkAssemblies.Count}   TPA entries: {TrustedPlatformAssemblies.Length}");
        w.WriteLine($"Projects (type!=package): {string.Join(", ", Projects.Values.Select(p => $"{p.Id}/{p.Version} [{p.Type}]"))}");
        w.WriteLine($"Packages ({Packages.Count}): id -> version");
        foreach (var p in Packages.Values.OrderBy(p => p.Id))
            w.WriteLine($"    {p.Id,-55} {p.Version,-10} asm: {string.Join(", ", p.Assemblies.Select(a => $"{a.Name} {a.AssemblyVersion}"))}");
        if (verbose)
        {
            w.WriteLine("Assembly -> package:");
            foreach (var a in Assemblies.Values.OrderBy(a => a.Name)) w.WriteLine($"    {a.Name,-55} -> {a.Owner}");
        }
    }
}

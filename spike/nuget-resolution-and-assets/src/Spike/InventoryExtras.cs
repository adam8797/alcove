using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.Loader;
using System.Text.Json;
using NuGet.Versioning;

namespace Spike;

/// <summary>Extra evidence for Q1: framework-provided packages, TPA, requested vs. resolved frameworks.</summary>
public static class InventoryExtras
{
    public static void Run(HostInventory host)
    {
        Console.WriteLine();
        Console.WriteLine("-- raw runtime properties");
        foreach (var k in new[] { "APP_CONTEXT_DEPS_FILES", "FX_DEPS_FILE", "PROBING_DIRECTORIES", "RUNTIME_IDENTIFIER", "APP_CONTEXT_BASE_DIRECTORY" })
            Console.WriteLine($"  {k} = {AppContext.GetData(k)}");

        Console.WriteLine("-- requested frameworks (runtimeconfig.json) vs resolved (deps file paths above)");
        var rc = Path.ChangeExtension(host.AppDepsFile, null);
        rc = rc[..^".deps".Length] + ".runtimeconfig.json";
        if (File.Exists(rc))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(rc));
            var ro = doc.RootElement.GetProperty("runtimeOptions");
            if (ro.TryGetProperty("framework", out var f)) Console.WriteLine($"  framework  : {f.GetProperty("name")} {f.GetProperty("version")}");
            if (ro.TryGetProperty("frameworks", out var fs)) foreach (var x in fs.EnumerateArray()) Console.WriteLine($"  frameworks : {x.GetProperty("name")} {x.GetProperty("version")}");
        }

        Console.WriteLine("-- System.Text.Json: host csproj references the 9.0.0 *package*");
        Console.WriteLine($"  in app deps.json as package? {host.Packages.ContainsKey("System.Text.Json")}");
        Console.WriteLine($"  framework assembly? {host.FrameworkAssemblies.TryGetValue("System.Text.Json", out var stj)} {stj?.AssemblyVersion} owner={stj?.Owner}");
        var loaded = typeof(System.Text.Json.JsonSerializer).Assembly;
        Console.WriteLine($"  loaded from: {loaded.Location}  version {loaded.GetName().Version}  ALC={AssemblyLoadContext.GetLoadContext(loaded)?.Name}");

        Console.WriteLine("-- TPA (= what the default context can bind to) split by origin");
        var fxDirs = host.Frameworks.Select(f => Path.GetDirectoryName(f.DepsFile)!).ToList();
        var fromFx = host.TrustedPlatformAssemblies.Count(p => fxDirs.Any(d => p.StartsWith(d)));
        Console.WriteLine($"  {host.TrustedPlatformAssemblies.Length} entries: {fromFx} from shared frameworks, {host.TrustedPlatformAssemblies.Length - fromFx} from the app dir");
        var tpaNames = host.TrustedPlatformAssemblies.Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = host.Assemblies.Keys.Concat(host.FrameworkAssemblies.Keys).Where(n => !tpaNames.Contains(n)).ToList();
        Console.WriteLine($"  deps.json assemblies not in TPA: {(missing.Count == 0 ? "none" : string.Join(", ", missing))}");

        // How well does "package id == framework assembly name && major <= fx major" reproduce the SDK's pruning data?
        // PackageOverrides.txt only exists in targeting packs (SDK machines), so this is a dev-time comparison.
        if (host.Frameworks.Count == 0 || !File.Exists(host.Frameworks[0].DepsFile)) return;
        var dotnetRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(host.Frameworks[0].DepsFile)!, "..", "..", ".."));
        foreach (var fx in host.Frameworks)
        {
            var refPack = Path.Combine(dotnetRoot, "packs", fx.Name + ".Ref");
            if (!Directory.Exists(refPack)) { Console.WriteLine($"-- no targeting pack for {fx.Name} at {refPack}"); continue; }
            var latest = Directory.GetDirectories(refPack).OrderBy(d => NuGetVersion.Parse(Path.GetFileName(d))).Last();
            var overrides = Path.Combine(latest, "data", "PackageOverrides.txt");
            if (!File.Exists(overrides)) continue;
            var lines = File.ReadAllLines(overrides).Where(l => l.Contains('|')).Select(l => l.Split('|')).ToList();
            var fxMajor = NuGetVersion.Parse(fx.Version).Major;
            var hit = lines.Where(l => CompartmentResolver.IsFrameworkProvided(host, l[0], VersionRange.Parse(l[1]))).ToList();
            var miss = lines.Except(hit).ToList();
            Console.WriteLine($"-- {fx.Name}: PackageOverrides.txt ({latest}) lists {lines.Count} packages; heuristic matches {hit.Count}; misses {miss.Count}:");
            foreach (var group in miss.GroupBy(l => l[0].StartsWith("runtime.") ? "runtime.* (old RID-split packages)" : "other"))
                Console.WriteLine($"    {group.Key}: {group.Count()}  e.g. {string.Join(", ", group.Take(6).Select(l => l[0] + "|" + l[1]))}");
            var fxExtra = host.FrameworkAssemblies.Values.Where(a => a.Owner == fx.Name && !lines.Any(l => l[0].Equals(a.Name, StringComparison.OrdinalIgnoreCase))).Select(a => a.Name).ToList();
            Console.WriteLine($"    framework assemblies with no package override entry: {fxExtra.Count} e.g. {string.Join(", ", fxExtra.Take(8))}");
        }
    }
}

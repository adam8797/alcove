using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;

string plugins = args.Length > 0 ? args[0]
    : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/WpfHost/bin/Debug/net10.0-windows/plugins"));
Console.WriteLine($"plugins: {plugins}");
if (!Directory.Exists(plugins)) { Console.WriteLine("build src/WpfHost first"); return 1; }

string Describe(Assembly? a) => a is null ? "null" : $"{a.FullName} [alc={AssemblyLoadContext.GetLoadContext(a)?.Name}, {Path.GetFileName(Path.GetDirectoryName(a.Location))}]";
string Try(Func<Assembly?> f) { try { return Describe(f()); } catch (Exception e) { return $"{e.GetType().Name}: {e.Message.Split('\n')[0]}"; } }

var results = new List<(string, bool)>();
void Expect(string what, string actual, string mustContain)
{
    bool ok = actual.Contains(mustContain);
    results.Add((what, ok));
    Console.WriteLine($"  {(ok ? "OK  " : "FAIL")} {what}\n         -> {actual}");
}

// ---- 1. WPF's lookup (verbatim algorithm) vs load order
var a = new FolderAlc("A", Path.Combine(plugins, "PluginA"));       // SharedControls 1.0.0.0 (v1)
var b = new FolderAlc("B", Path.Combine(plugins, "PluginB"));       // SharedControls 2.0.0.0 (v2)
var c = new FolderAlc("C", Path.Combine(plugins, "PluginC"));       // SharedControls 1.0.0.0 (v1b)
var d = new FolderAlc("D", Path.Combine(plugins, "PluginD"));       // SharedControls 1.0.0.0 (v1), never loaded eagerly
var rf = new FolderAlc("RF", Path.Combine(plugins, "PluginA-rollforward")); // SharedControls 2.0.0.0 only

Console.WriteLine("\n[1] Default context alone: Assembly.Load(\"SharedControls, Version=1.0.0.0\") from a default-context caller (= PresentationFramework's situation)");
Expect("default-context Assembly.Load fails when nobody loaded SharedControls", Try(() => Assembly.Load("SharedControls, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null")), "FileNotFoundException");

Console.WriteLine("\n[2] Same call inside d.EnterContextualReflection()  (prediction for scenario 9b)");
using (d.EnterContextualReflection())
    Expect("contextual reflection routes the default-context Assembly.Load to compartment D", Try(() => Assembly.Load("SharedControls, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null")), "alc=D");

Console.WriteLine("\n[3] Load v1 in A, then v2 in B, then v1b in C. AppDomain.GetAssemblies() order:");
a.LoadFromAssemblyName(new AssemblyName("SharedControls"));
b.LoadFromAssemblyName(new AssemblyName("SharedControls"));
c.LoadFromAssemblyName(new AssemblyName("SharedControls"));
foreach (var x in AppDomain.CurrentDomain.GetAssemblies().Where(x => x.GetName().Name == "SharedControls")) Console.WriteLine($"     {Describe(x)}");
Expect("WPF lookup by name only -> LAST loaded (C's v1b)", Describe(WpfLookup.GetLoadedAssembly(new AssemblyName("SharedControls"))), "alc=C");
Expect("WPF lookup with Version=1.0.0.0 -> last loaded 1.0.0.0 = C's v1b, not A's v1 (scenario 7)", Describe(WpfLookup.GetLoadedAssembly(new AssemblyName("SharedControls, Version=1.0.0.0"))), "alc=C");
Expect("WPF lookup with Version=2.0.0.0 -> B's v2 (scenario 5)", Describe(WpfLookup.GetLoadedAssembly(new AssemblyName("SharedControls, Version=2.0.0.0"))), "alc=B");

Console.WriteLine("\n[4] Roll-forward: compartment RF only has SharedControls 2.0.0.0; a request for 1.0.0.0 (scenario 10)");
rf.LoadFromAssemblyName(new AssemblyName("SharedControls"));
Expect("custom ALC Load() may return 2.0.0.0 for a 1.0.0.0 request (10b relies on this)",
    Try(() => rf.LoadFromAssemblyName(new AssemblyName("SharedControls, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null"))), "2.0.0.0");

Console.WriteLine("\n[5] Default.Resolving forwarder returning another context's assembly (scenarios 9d/10c)");
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    if (name.Name != "SharedControls") return null;
    var asm = rf.LoadFromAssemblyName(new AssemblyName("SharedControls"));
    Console.WriteLine($"         (Default.Resolving: {name.FullName} -> {asm.FullName} from RF)");
    return asm;
};
// The A/B/C copies are loaded but in other ALCs; the default context still doesn't have one, so Resolving fires.
Expect("default-context Assembly.Load(1.0.0.0) is satisfied by the forwarder with RF's 2.0.0.0",
    Try(() => Assembly.Load("SharedControls, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null")), "alc=RF");

Console.WriteLine();
Console.WriteLine(results.All(r => r.Item2) ? "ALL OK" : "SOME CHECKS FAILED");
return results.All(r => r.Item2) ? 0 : 1;

sealed class FolderAlc(string name, string folder) : AssemblyLoadContext(name)
{
    protected override Assembly? Load(AssemblyName n)
    {
        var existing = Assemblies.FirstOrDefault(a => a.GetName().Name == n.Name);
        if (existing != null) return existing;
        var p = Path.Combine(folder, n.Name + ".dll");
        return File.Exists(p) ? LoadFromAssemblyPath(p) : null;
    }
}

/// <summary>Verbatim logic of dotnet/wpf release/10.0 src/Microsoft.DotNet.Wpf/src/Shared/MS/Internal/SafeSecurityHelper.cs GetLoadedAssembly (cache removed).</summary>
static class WpfLookup
{
    public static Assembly? GetLoadedAssembly(AssemblyName assemblyName)
    {
        Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
        Version? reqVersion = assemblyName.Version;
        CultureInfo? reqCulture = assemblyName.CultureInfo;
        byte[]? reqKeyToken = assemblyName.GetPublicKeyToken();
        for (int i = assemblies.Length - 1; i >= 0; i--)
        {
            AssemblyName cur = new(assemblies[i].FullName!);
            if (string.Equals(cur.Name, assemblyName.Name, StringComparison.InvariantCultureIgnoreCase) &&
                (reqVersion is null || reqVersion.Equals(cur.Version)) &&
                (reqCulture is null || reqCulture.Equals(cur.CultureInfo)) &&
                (reqKeyToken is null || IsSameKeyToken(reqKeyToken, cur.GetPublicKeyToken())))
                return assemblies[i];
        }
        return null;
    }
    static bool IsSameKeyToken(byte[]? a, byte[]? b) =>
        (a is null && b is null) || (a is not null && b is not null && a.AsSpan().SequenceEqual(b));
}

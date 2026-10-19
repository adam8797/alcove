using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace Host;

public enum DelegateStrategy
{
    /// <summary>other.LoadFromAssemblyName(name): asks the providing compartment, which loads on demand.</summary>
    AskProvider,
    /// <summary>Only return an assembly the providing compartment has ALREADY loaded.</summary>
    AlreadyLoadedOnly,
}

/// <summary>Minimal compartment implementing the architecture §5 resolution order, driven by tables.</summary>
public sealed class Compartment(string name) : AssemblyLoadContext(name, isCollectible: false)
{
    static readonly string FrameworkDir = RuntimeEnvironment.GetRuntimeDirectory();

    public HashSet<string> HostOwned { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Plugin-provided assemblies AND exposed dependencies: name -> providing compartment.</summary>
    public Dictionary<string, Compartment> ProvidedBy { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Private { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> HostShared { get; } = new(StringComparer.OrdinalIgnoreCase);
    public DelegateStrategy Strategy { get; set; } = DelegateStrategy.AskProvider;
    public List<string> Trace { get; } = [];

    bool IsFramework(string n) => File.Exists(Path.Combine(FrameworkDir, n + ".dll"));

    protected override Assembly? Load(AssemblyName an)
    {
        var n = an.Name!;
        if (IsFramework(n)) return null; // shared framework: host-owned, not traced (noise)
        if (HostOwned.Contains(n)) { Trace.Add($"[{Name}] Load({n} {an.Version}) -> 1 host-owned -> null"); return null; }
        if (ProvidedBy.TryGetValue(n, out var other))
        {
            Assembly? a = Strategy switch
            {
                DelegateStrategy.AskProvider => other.LoadFromAssemblyName(an),
                _ => other.Assemblies.FirstOrDefault(x => string.Equals(x.GetName().Name, n, StringComparison.OrdinalIgnoreCase)),
            };
            Trace.Add($"[{Name}] Load({n} {an.Version}) -> 2 provided by '{other.Name}' ({Strategy}) -> {(a is null ? "null" : $"{a.GetName().Version} @'{GetLoadContext(a)?.Name}'")}");
            return a;
        }
        if (Private.TryGetValue(n, out var path))
        {
            var a = LoadFromAssemblyPath(path);
            Trace.Add($"[{Name}] Load({n} {an.Version}) -> 3 private -> {a.GetName().Version} from {Rel(path)}");
            return a;
        }
        if (HostShared.Contains(n)) { Trace.Add($"[{Name}] Load({n} {an.Version}) -> 4 host-shared -> null"); return null; }
        Trace.Add($"[{Name}] Load({n} {an.Version}) -> 5 unknown -> null");
        return null;
    }

    static string Rel(string p) => p[(p.IndexOf("packages/", StringComparison.Ordinal) is var i and >= 0 ? i : 0)..];
}

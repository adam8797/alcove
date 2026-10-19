using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using HostSdk;

namespace WpfHost;

/// <summary>
/// Minimal compartment: one non-collectible AssemblyLoadContext over one plugin folder, following the
/// resolution order of docs/architecture.md §5 (host-owned → null, plugin-provided by another compartment →
/// that assembly, compartment-private → load from folder, everything else → null / default context).
/// </summary>
public sealed class Compartment : AssemblyLoadContext
{
    private static readonly HashSet<string> HostOwned = new(StringComparer.OrdinalIgnoreCase) { "HostSdk" };

    private readonly Dictionary<string, Assembly> _imports = new(StringComparer.OrdinalIgnoreCase);

    public string Folder { get; }

    public static List<Compartment> Registry { get; } = new();

    public Compartment(string name, string folder) : base(name, isCollectible: false)
    {
        Folder = folder;
        Registry.Add(this);
    }

    /// <summary>Plugin-provided assembly from another compartment (cross-compartment plugin dependency).</summary>
    public Compartment Import(Assembly assembly)
    {
        _imports[assembly.GetName().Name!] = assembly;
        return this;
    }

    public bool HasPrivate(string simpleName) => File.Exists(Path.Combine(Folder, simpleName + ".dll"));

    protected override Assembly? Load(AssemblyName name)
    {
        string simple = name.Name!;
        if (HostOwned.Contains(simple))
        {
            Log($"Load({name.FullName}) -> null (host-owned, default context)");
            return null;
        }

        if (_imports.TryGetValue(simple, out var imported))
        {
            Log($"Load({name.FullName}) -> {imported.FullName} from compartment '{GetLoadContext(imported)?.Name}' (plugin-provided)");
            return imported;
        }

        // Already loaded here (e.g. a version-mismatched request after eager loading): reuse it.
        var existing = Assemblies.FirstOrDefault(a => string.Equals(a.GetName().Name, simple, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            Log($"Load({name.FullName}) -> {existing.FullName} (already loaded in this compartment)");
            return existing;
        }

        string path = Path.Combine(Folder, simple + ".dll");
        if (File.Exists(path))
        {
            var asm = LoadFromAssemblyPath(path);
            Log($"Load({name.FullName}) -> {asm.FullName} from {Path.GetRelativePath(AppContext.BaseDirectory, path)} (compartment-private)");
            return asm;
        }

        if (!IsFramework(simple))
            Log($"Load({name.FullName}) -> null (unknown; default context)");
        return null;
    }

    /// <summary>Loads every DLL in the folder into this compartment (what a loader may do eagerly).</summary>
    public void EagerLoadAll()
    {
        foreach (var file in Directory.GetFiles(Folder, "*.dll").Order())
            LoadFromAssemblyName(new AssemblyName(Path.GetFileNameWithoutExtension(file)));
    }

    public Assembly LoadByName(string simpleName) => LoadFromAssemblyName(new AssemblyName(simpleName));

    public ISpikePlugin CreateEntry(string pluginAssembly)
    {
        var asm = LoadByName(pluginAssembly);
        var type = asm.GetExportedTypes().Single(t => typeof(ISpikePlugin).IsAssignableFrom(t) && !t.IsAbstract);
        return (ISpikePlugin)Activator.CreateInstance(type)!;
    }

    private void Log(string message) => Console.WriteLine($"      · [alc {Name}] {message}");

    private static bool IsFramework(string n) =>
        n.StartsWith("System", StringComparison.Ordinal) || n.StartsWith("Microsoft", StringComparison.Ordinal) ||
        n.StartsWith("Presentation", StringComparison.Ordinal) || n.StartsWith("Windows", StringComparison.Ordinal) ||
        n is "netstandard" or "mscorlib" or "UIAutomationTypes" or "UIAutomationProvider" or "ReachFramework";

    /// <summary>
    /// Mitigation prototype: a handler on the DEFAULT context that forwards failed loads to the one compartment
    /// that owns a private copy with that simple name (ignoring version). WPF's internal Assembly.Load calls run
    /// in the default context (PresentationFramework's), so this is where they end up when nothing matched.
    /// </summary>
    public static void InstallDefaultContextForwarder()
    {
        Default.Resolving += (_, name) =>
        {
            var owners = Registry.Where(c => c.HasPrivate(name.Name!)).ToList();
            if (owners.Count != 1)
            {
                Console.WriteLine($"      · [default.Resolving] {name.FullName}: {owners.Count} owning compartments -> not forwarded");
                return null;
            }
            var asm = owners[0].LoadByName(name.Name!);
            Console.WriteLine($"      · [default.Resolving] {name.FullName} -> {asm.FullName} from compartment '{owners[0].Name}'");
            return asm;
        };
    }
}

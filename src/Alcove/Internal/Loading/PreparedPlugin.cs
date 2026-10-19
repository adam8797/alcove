using System.Reflection;
using Alcove.Internal.Cells;

namespace Alcove.Internal.Loading;

/// <summary>
/// A plugin whose assemblies are loaded into its cell and whose entry point is created: everything activation does
/// before <see cref="IPlugin.Register"/>. A failure is kept, with its stage, for activation to report.
/// </summary>
internal sealed class PreparedPlugin
{
    private PreparedPlugin(IReadOnlyList<Assembly> assemblies, IPlugin? entryPoint, Exception? fault, string stage)
    {
        Assemblies = assemblies;
        EntryPoint = entryPoint;
        Fault = fault;
        Stage = stage;
    }

    /// <summary>The plugin's own assemblies, loaded into its cell.</summary>
    public IReadOnlyList<Assembly> Assemblies { get; }

    /// <summary>The entry point instance, if the plugin has one and it was created.</summary>
    public IPlugin? EntryPoint { get; }

    /// <summary>Why preparing failed, or <see langword="null"/>.</summary>
    public Exception? Fault { get; }

    /// <summary>The stage that failed (e.g. "loading its assemblies"), for the fault reason.</summary>
    public string Stage { get; }

    /// <summary>Loads the plugin's assemblies into its cell and creates its entry point (inside the cell's contextual reflection scope).</summary>
    public static PreparedPlugin Prepare(PlannedPlugin planned, Cell cell, CellPlan plan)
    {
        var stage = "loading its assemblies";
        IReadOnlyList<Assembly> assemblies = [];
        try
        {
            assemblies = LoadAssemblies(planned, cell, plan);
            if (planned.EntryPoint is not { } entryPoint)
            {
                return new PreparedPlugin(assemblies, null, null, "");
            }

            stage = $"creating its entry point {entryPoint}";
            using (cell.EnterContextualReflection())
            {
                return new PreparedPlugin(assemblies, CreateEntryPoint(FindEntryPointType(assemblies, entryPoint)), null, "");
            }
        }
        catch (Exception ex)
        {
            return new PreparedPlugin(assemblies, null, ex is TargetInvocationException { InnerException: { } inner } ? inner : ex, stage);
        }
    }

    private static List<Assembly> LoadAssemblies(PlannedPlugin planned, Cell cell, CellPlan plan) =>
        plan.Private.Values
            .Where(a => string.Equals(a.PackageId, planned.Id, StringComparison.OrdinalIgnoreCase))
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Select(a => cell.LoadFromAssemblyName(new AssemblyName(a.Name)))
            .ToList();

    private static Type FindEntryPointType(IReadOnlyList<Assembly> assemblies, string entryPoint)
    {
        var type = assemblies.Select(a => a.GetType(entryPoint, throwOnError: false, ignoreCase: false)).FirstOrDefault(t => t is not null)
            ?? throw new TypeLoadException(
                $"The entry point {entryPoint} isn't a type in the plugin's assemblies ({string.Join(", ", assemblies.Select(a => a.GetName().Name))}).");
        if (!typeof(IPlugin).IsAssignableFrom(type))
        {
            throw new InvalidCastException($"The entry point {type} doesn't implement {typeof(IPlugin)}.");
        }

        return type;
    }

    private static IPlugin CreateEntryPoint(Type type)
    {
        if (type.IsAbstract || type.GetConstructor(Type.EmptyTypes) is not { IsPublic: true } constructor)
        {
            throw new MissingMethodException($"The entry point {type} needs a public parameterless constructor.");
        }

        return (IPlugin)constructor.Invoke(null);
    }
}

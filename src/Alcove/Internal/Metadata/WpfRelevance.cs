using System.Collections.Immutable;

namespace Alcove.Internal.Metadata;

/// <summary>Decides which assemblies are <b>WPF-relevant</b> (CONTEXT.md).</summary>
internal static class WpfRelevance
{
    /// <summary>
    /// The simple names of the WPF-relevant assemblies among those loaded into one load context: an assembly is
    /// relevant if it contains <c>&lt;SimpleName&gt;.g.resources</c>, or if an assembly in the same context that
    /// does references it (AssemblyRef table or BAML). Only names of <paramref name="loadContextAssemblies"/> are returned.
    /// </summary>
    public static ImmutableHashSet<string> FindRelevant(IEnumerable<WpfAssemblyInfo> loadContextAssemblies)
    {
        ArgumentNullException.ThrowIfNull(loadContextAssemblies);

        var assemblies = loadContextAssemblies.ToList();
        var present = assemblies.Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var relevant = ImmutableHashSet.CreateBuilder<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var assembly in assemblies.Where(a => a.HasWpfResources))
        {
            relevant.Add(assembly.Name);
            foreach (var name in assembly.ReferencedNames)
            {
                if (present.Contains(name))
                {
                    relevant.Add(name);
                }
            }
        }

        return relevant.ToImmutable();
    }
}

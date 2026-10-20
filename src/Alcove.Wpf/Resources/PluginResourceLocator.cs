using System.Reflection;
using System.Resources;

namespace Alcove.Wpf.Resources;

/// <summary>
/// Which resource dictionaries of a plugin assembly the host merges: those it declares with
/// <see cref="PluginResourcesAttribute"/>, otherwise <c>Themes/Plugin.xaml</c> if it has that part.
/// </summary>
internal static class PluginResourceLocator
{
    /// <summary>The conventional dictionary.</summary>
    public const string ConventionPath = "Themes/Plugin.xaml";

    /// <summary>The paths of the dictionaries to merge from <paramref name="assembly"/>, in order.</summary>
    public static IReadOnlyList<string> Find(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        // Read by name: the attribute type is the host's (Alcove.Wpf is host-owned), but reading the data needs no type identity.
        var declared = assembly.GetCustomAttributesData()
            .Where(a => a.AttributeType.FullName == typeof(PluginResourcesAttribute).FullName)
            .Select(a => a.ConstructorArguments is [{ Value: string path }] ? path : null)
            .OfType<string>()
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();
        if (declared.Count > 0)
        {
            return declared;
        }

        return HasPart(assembly, ConventionPath) ? [ConventionPath] : [];
    }

    /// <summary>Whether the assembly's compiled XAML (<c>&lt;Name&gt;.g.resources</c>) holds the part at <paramref name="path"/>, as BAML or loose XAML.</summary>
    public static bool HasPart(Assembly assembly, string path)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var key = path.Replace('\\', '/').TrimStart('/').ToLowerInvariant();
        var baml = System.IO.Path.ChangeExtension(key, ".baml");
        using var stream = assembly.GetManifestResourceStream(assembly.GetName().Name + ".g.resources");
        if (stream is null)
        {
            return false;
        }

        using var reader = new ResourceReader(stream);
        var entries = reader.GetEnumerator();
        while (entries.MoveNext())
        {
            if (entries.Key is string name && (string.Equals(name, baml, StringComparison.Ordinal) || string.Equals(name, key, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }
}

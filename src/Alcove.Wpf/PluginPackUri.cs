using System.Reflection;
using System.Text;

namespace Alcove.Wpf;

/// <summary>
/// Builds <c>pack://</c> URIs that always name the version (and public key token) of the loaded assembly:
/// <c>/Name;v1.2.3.4;0123456789abcdef;component/Themes/Plugin.xaml</c>.
/// </summary>
/// <remarks>
/// WPF finds the assembly a pack URI names by scanning every assembly loaded in the process, in every cell, and takes
/// the most recently loaded one with that simple name unless the URI names a version. A URI without one can therefore
/// reach another cell's copy of the assembly. Use these URIs whenever a plugin refers to its own or another assembly's
/// XAML from code (e.g. <c>ResourceDictionary.Source</c>, <c>Application.LoadComponent</c>).
/// </remarks>
public static partial class PluginPackUri
{
    /// <summary>The relative pack URI of a XAML part (or other resource) of a loaded assembly, as a string.</summary>
    /// <param name="assembly">The assembly holding the part.</param>
    /// <param name="path">The part's path in the project, e.g. <c>Themes/Plugin.xaml</c>.</param>
    /// <returns>E.g. <c>/MyPlugin;v1.2.3.0;component/Themes/Plugin.xaml</c>.</returns>
    public static string Format(Assembly assembly, string path)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return Format(assembly.GetName(), path);
    }

    /// <summary>The relative pack URI of a part of the assembly with this identity, as a string.</summary>
    /// <param name="assemblyName">The assembly's identity: simple name, version and (if strong-named) public key token.</param>
    /// <param name="path">The part's path in the project, e.g. <c>Themes/Plugin.xaml</c>.</param>
    /// <returns>E.g. <c>/MyPlugin;v1.2.3.0;component/Themes/Plugin.xaml</c>; the version is left out only if the name has none.</returns>
    public static string Format(AssemblyName assemblyName, string path)
    {
        ArgumentNullException.ThrowIfNull(assemblyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (string.IsNullOrEmpty(assemblyName.Name))
        {
            throw new ArgumentException("The assembly name has no simple name.", nameof(assemblyName));
        }

        var part = path.Replace('\\', '/').TrimStart('/');
        if (part.Length == 0)
        {
            throw new ArgumentException("The path names no part.", nameof(path));
        }

        var uri = new StringBuilder("/").Append(assemblyName.Name);
        if (assemblyName.Version is { } version)
        {
            uri.Append(";v").Append(version);
        }

        if (assemblyName.GetPublicKeyToken() is { Length: > 0 } token)
        {
            uri.Append(';').Append(Convert.ToHexStringLower(token));
        }

        return uri.Append(";component/").Append(part).ToString();
    }

    /// <summary>The relative pack URI of a XAML part of a loaded assembly, e.g. for <c>ResourceDictionary.Source</c>.</summary>
    /// <param name="assembly">The assembly holding the part.</param>
    /// <param name="path">The part's path in the project, e.g. <c>Themes/Plugin.xaml</c>.</param>
    /// <returns>A relative <see cref="Uri"/> (see <see cref="Format(Assembly, string)"/>).</returns>
    public static Uri Create(Assembly assembly, string path) => new(Format(assembly, path), UriKind.Relative);
}

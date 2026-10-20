using System.IO.Packaging;
using System.Reflection;

namespace Alcove.Wpf.Resources;

public static partial class PluginPackUri
{
    /// <summary>The absolute pack URI of a part of a loaded assembly: <c>pack://application:,,,/Name;v1.2.3.4;component/path</c>.</summary>
    /// <param name="assembly">The assembly holding the part.</param>
    /// <param name="path">The part's path in the project, e.g. <c>Themes/Plugin.xaml</c>.</param>
    /// <returns>An absolute <see cref="Uri"/> (see <see cref="Format(Assembly, string)"/>).</returns>
    public static Uri CreateAbsolute(Assembly assembly, string path)
    {
        // Registers the pack:// scheme with System.Uri, as WPF does on startup.
        _ = PackUriHelper.UriSchemePack;
        return new Uri("pack://application:,,," + Format(assembly, path), UriKind.Absolute);
    }
}

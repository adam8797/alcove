namespace Alcove.Wpf;

/// <summary>
/// Names a resource dictionary of this plugin assembly that the host merges into its application resources when the
/// plugin loads (instead of the convention, <c>Themes/Plugin.xaml</c>):
/// <code>[assembly: PluginResources("Themes/Colors.xaml")]</code>
/// </summary>
/// <remarks>
/// Applies with <c>options.UseWpf(...)</c> when <see cref="WpfPluginOptions.MergePluginResources"/> is on. Repeat it to
/// merge several dictionaries, in the order declared. An assembly that declares none gets its <c>Themes/Plugin.xaml</c>
/// merged, if it has one. A declared dictionary that doesn't exist faults the plugin.
/// </remarks>
/// <param name="path">The dictionary's path in the project, e.g. <c>Themes/Colors.xaml</c>.</param>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true, Inherited = false)]
public sealed class PluginResourcesAttribute(string path) : Attribute
{
    /// <summary>The dictionary's path in the project, e.g. <c>Themes/Colors.xaml</c>.</summary>
    public string Path { get; } = path;
}

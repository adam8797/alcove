[assembly: Alcove.Wpf.Resources.PluginResources("Themes/Colors.xaml")]
[assembly: Alcove.Wpf.Resources.PluginResources("Themes\\Extra.xaml")]

namespace Alcove.Wpf.Resources
{
    /// <summary>Stands in for Alcove.Wpf's attribute, which hosts read by name.</summary>
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    internal sealed class PluginResourcesAttribute(string path) : Attribute
    {
        public string Path { get; } = path;
    }
}

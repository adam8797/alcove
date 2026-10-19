[assembly: Alcove.Wpf.PluginResources("Themes/Colors.xaml")]
[assembly: Alcove.Wpf.PluginResources("Themes\\Extra.xaml")]

namespace Alcove.Wpf
{
    /// <summary>Stands in for Alcove.Wpf's attribute, which hosts read by name.</summary>
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    internal sealed class PluginResourcesAttribute(string path) : Attribute
    {
        public string Path { get; } = path;
    }
}

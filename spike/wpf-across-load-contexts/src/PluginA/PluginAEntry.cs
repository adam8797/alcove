using System.Windows;
using HostSdk;

namespace PluginA;

public sealed class PluginAEntry : ISpikePlugin
{
    public string Id => "PluginA";
    public FrameworkElement CreateView() => new PluginAView();
    public object? CreateViewModel() => new ViewModels.AItemViewModel();
    public Uri? ThemeUri => new("/PluginA;component/Themes/Plugin.xaml", UriKind.Relative);
}

using System.Windows;

namespace HostSdk;

/// <summary>Minimal extension point for the spike. Each plugin assembly has one public implementation.</summary>
public interface ISpikePlugin
{
    string Id { get; }

    /// <summary>Creates the plugin's main view (a UserControl with compiled XAML).</summary>
    FrameworkElement CreateView();

    /// <summary>Creates a view-model whose view is supplied by an implicit DataTemplate in the plugin's dictionary.</summary>
    object? CreateViewModel() => null;

    /// <summary>Relative pack URI of the plugin's resource dictionary, e.g. "/PluginA;component/Themes/Plugin.xaml".</summary>
    Uri? ThemeUri => null;
}

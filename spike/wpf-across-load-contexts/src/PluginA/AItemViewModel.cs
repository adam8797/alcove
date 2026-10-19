namespace PluginA.ViewModels;

/// <summary>View-model whose view is an implicit DataTemplate in Themes/Plugin.xaml.</summary>
public class AItemViewModel
{
    public string Label => "MARK:PluginA.AItemViewModel.Template";
    public override string ToString() => "MARK:PluginA.AItemViewModel.ToString(no template applied)";
}

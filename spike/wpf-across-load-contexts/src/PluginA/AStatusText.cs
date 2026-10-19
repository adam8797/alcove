using System.Windows.Controls;

namespace PluginA;

/// <summary>A type defined in the plugin's own assembly, used from XAML (here and from PluginB).</summary>
public class AStatusText : TextBlock
{
    public AStatusText() => Text = "MARK:PluginA.AStatusText";
}

using System.Windows;
using System.Windows.Controls;

namespace SharedControls;

/// <summary>Lookless custom control whose template comes from Themes/Generic.xaml (SystemResources path).</summary>
public class FancyBox : Control
{
    static FancyBox()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(FancyBox), new FrameworkPropertyMetadata(typeof(FancyBox)));
    }
}

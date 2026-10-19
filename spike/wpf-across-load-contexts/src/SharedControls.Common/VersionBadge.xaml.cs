using System.Windows.Controls;

namespace SharedControls;

/// <summary>UserControl with compiled XAML. Its XAML differs per build variant (v1 / v1b / v2).</summary>
public partial class VersionBadge : UserControl
{
    public VersionBadge() => InitializeComponent();   // generated URI: /SharedControls;V<AssemblyVersion>;component/versionbadge.xaml

    /// <summary>Variant compiled into *this* assembly's code (not XAML).</summary>
    public static string CodeVariant => SharedInfo.Variant;
}

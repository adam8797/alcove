using System.Windows;
using System.Windows.Controls;

namespace SharedControls;

/// <summary>
/// Same as VersionBadge, but loads its BAML with an UNVERSIONED pack URI. This is exactly what the markup
/// compiler generates for a library that does not set the AssemblyVersion MSBuild property explicitly
/// (the SDK default: AssemblyVersion is only computed from Version later, in the GetAssemblyVersion target,
/// after MarkupCompilePass1 has run). Verified on Linux: such a project generates "/Name;component/x.xaml".
/// </summary>
public partial class UnversionedBadge : UserControl
{
    public UnversionedBadge()
    {
        // Deliberately not calling the generated InitializeComponent() (which would use ";V1.0.0.0;").
        _contentLoaded = true;
        Application.LoadComponent(this, new Uri("/SharedControls;component/unversionedbadge.xaml", UriKind.Relative));
    }
}

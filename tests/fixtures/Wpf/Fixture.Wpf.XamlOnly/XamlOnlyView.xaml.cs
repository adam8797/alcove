using System.Windows;
using System.Windows.Controls;
using Alcove;
using Fixture.Wpf.HostSdk;

namespace Fixture.Wpf.XamlOnly;

public partial class XamlOnlyView : UserControl
{
    public XamlOnlyView() => InitializeComponent();
}

[Extension(typeof(IViewFactory))]
public sealed class XamlOnlyFactory : IViewFactory
{
    public string Name => "XamlOnly";

    public FrameworkElement CreateView() => new XamlOnlyView();
}

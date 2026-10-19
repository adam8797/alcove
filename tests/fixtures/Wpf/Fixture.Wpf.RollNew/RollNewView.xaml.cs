using System.Windows;
using System.Windows.Controls;
using Alcove;
using Fixture.Wpf.HostSdk;

namespace Fixture.Wpf.RollNew;

public partial class RollNewView : UserControl
{
    public RollNewView() => InitializeComponent();
}

[Extension(typeof(IViewFactory))]
public sealed class RollNewFactory : IViewFactory
{
    public string Name => "RollNew";

    public FrameworkElement CreateView() => new RollNewView();
}

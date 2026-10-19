using System.Windows;
using System.Windows.Controls;
using Alcove;
using Fixture.Wpf.HostSdk;

namespace Fixture.Wpf.RollOld;

public partial class RollOldView : UserControl
{
    public RollOldView() => InitializeComponent();
}

[Extension(typeof(IViewFactory))]
public sealed class RollOldFactory : IViewFactory
{
    public string Name => "RollOld";

    public FrameworkElement CreateView() => new RollOldView();
}

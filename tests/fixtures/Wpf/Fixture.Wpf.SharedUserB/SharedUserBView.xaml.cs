using System.Windows;
using System.Windows.Controls;
using Alcove;
using Fixture.Wpf.HostSdk;

namespace Fixture.Wpf.SharedUserB;

public partial class SharedUserBView : UserControl
{
    public SharedUserBView() => InitializeComponent();
}

[Extension(typeof(IViewFactory))]
public sealed class SharedUserBFactory : IViewFactory
{
    public string Name => "SharedUserB";

    public FrameworkElement CreateView() => new SharedUserBView();
}

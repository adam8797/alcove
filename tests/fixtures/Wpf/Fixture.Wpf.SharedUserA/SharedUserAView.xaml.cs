using System.Windows;
using System.Windows.Controls;
using Alcove;
using Fixture.Wpf.HostSdk;

namespace Fixture.Wpf.SharedUserA;

public partial class SharedUserAView : UserControl
{
    public SharedUserAView() => InitializeComponent();
}

[Extension(typeof(IViewFactory))]
public sealed class SharedUserAFactory : IViewFactory
{
    public string Name => "SharedUserA";

    public FrameworkElement CreateView() => new SharedUserAView();
}

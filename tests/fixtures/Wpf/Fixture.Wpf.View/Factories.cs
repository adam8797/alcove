using System.Windows;
using Alcove;
using Fixture.Wpf.HostSdk;
using Fixture.Wpf.View.ViewModels;

namespace Fixture.Wpf.View;

[Extension(typeof(IViewFactory))]
public sealed class HelloFactory : IViewFactory
{
    public string Name => "Hello";

    public FrameworkElement CreateView() => new HelloView();
}

[Extension(typeof(IViewModelFactory))]
public sealed class GreetingFactory : IViewModelFactory
{
    public object CreateViewModel() => new GreetingViewModel();
}

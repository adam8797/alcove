using System.Windows;
using System.Windows.Media;
using Alcove.Wpf.Resources;

namespace Fixture.Wpf.HostSdk;

/// <summary>An extension point: a named view.</summary>
public interface IViewFactory
{
    string Name { get; }

    FrameworkElement CreateView();
}

/// <summary>An extension point: a view-model, shown through the view-to-view-model convention.</summary>
public interface IViewModelFactory
{
    object CreateViewModel();
}

/// <summary>Resource keys the host defines and plugins use.</summary>
public static class HostKeys
{
    public static readonly SharedResourceKey<Brush> Accent = new("Fixture.Wpf.HostSdk.Accent");
}

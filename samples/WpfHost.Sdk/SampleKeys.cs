using System.Windows;
using System.Windows.Media;
using Alcove.Wpf;

namespace WpfHost.Sdk;

/// <summary>
/// Resources the host defines for every plugin: <c>{DynamicResource {x:Static sdk:SampleKeys.AccentBrush}}</c>. The host
/// sets the values (and may change them at run time, e.g. for a theme switch); plugins only use the keys.
/// </summary>
public static class SampleKeys
{
    public static readonly SharedResourceKey<Brush> AccentBrush = new("WpfHost.Sdk.AccentBrush");

    public static readonly SharedResourceKey<Brush> PanelBackground = new("WpfHost.Sdk.PanelBackground");

    public static readonly SharedResourceKey<Style> HeaderTextStyle = new("WpfHost.Sdk.HeaderTextStyle");
}

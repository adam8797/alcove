using System.Windows;
using System.Windows.Controls;

namespace WpfHost.Sdk;

/// <summary>A panel title in the host's style.</summary>
public partial class PanelHeader : UserControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(PanelHeader), new PropertyMetadata(""));

    public PanelHeader() => InitializeComponent();

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }
}

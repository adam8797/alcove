using System.Windows;
using System.Windows.Controls;
using WpfHost.Sdk;

namespace Sample.Charts;

public partial class PanelView : UserControl
{
    public PanelView() => InitializeComponent();
}

[ToolPanel("Charts")]
public sealed class ChartsPanel : IToolPanel
{
    public FrameworkElement CreateContent() => new PanelView();
}

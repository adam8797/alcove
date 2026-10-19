using System.Windows;
using System.Windows.Controls;
using WpfHost.Sdk;

namespace Sample.Clock;

public partial class PanelView : UserControl
{
    private readonly System.Windows.Threading.DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    public PanelView()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => Update();
        Loaded += (_, _) => { Update(); _timer.Start(); };
        Unloaded += (_, _) => _timer.Stop();
    }

    private void Update()
    {
        var now = DateTime.Now;
        Time.Text = now.ToLongTimeString();
        Seconds.Value = now.Second * 100.0 / 59;
    }
}

[ToolPanel("Clock")]
public sealed class ClockPanel : IToolPanel
{
    public FrameworkElement CreateContent() => new PanelView();
}

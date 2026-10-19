using System.Windows;
using HostSdk;

namespace PluginD;

public sealed class PluginDEntry : ISpikePlugin
{
    public string Id => "PluginD";
    public FrameworkElement CreateView() => new PluginDView();
}

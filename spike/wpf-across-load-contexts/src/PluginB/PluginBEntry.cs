using System.Windows;
using HostSdk;

namespace PluginB;

public sealed class PluginBEntry : ISpikePlugin
{
    public string Id => "PluginB";
    public FrameworkElement CreateView() => new PluginBView();
}

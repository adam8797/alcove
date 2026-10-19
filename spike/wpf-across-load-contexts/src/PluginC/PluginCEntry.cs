using System.Windows;
using HostSdk;

namespace PluginC;

public sealed class PluginCEntry : ISpikePlugin
{
    public string Id => "PluginC";
    public FrameworkElement CreateView() => new PluginCView();
}

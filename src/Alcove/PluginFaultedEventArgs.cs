namespace Alcove;

/// <summary>Data of <see cref="PluginHost.PluginFaulted"/>.</summary>
public sealed class PluginFaultedEventArgs : EventArgs
{
    internal PluginFaultedEventArgs(PluginInfo plugin, Exception fault)
    {
        Plugin = plugin;
        Fault = fault;
    }

    /// <summary>The plugin, now <see cref="PluginStatus.Faulted"/>.</summary>
    public PluginInfo Plugin { get; }

    /// <summary>The exception the plugin threw.</summary>
    public Exception Fault { get; }
}

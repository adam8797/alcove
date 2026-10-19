namespace Alcove;

/// <summary>Data of <see cref="ExtensionCatalog.ExtensionsAdded"/>: the extensions one plugin added.</summary>
public sealed class ExtensionsAddedEventArgs : EventArgs
{
    internal ExtensionsAddedEventArgs(PluginInfo plugin, IReadOnlyList<Extension> extensions)
    {
        Plugin = plugin;
        Extensions = extensions;
    }

    /// <summary>The plugin that was loaded.</summary>
    public PluginInfo Plugin { get; }

    /// <summary>
    /// Its extensions, in registration order. Each is an <see cref="Extension{T}"/> of its
    /// <see cref="Extension.ExtensionPoint"/>, e.g. <c>e.Extensions.OfType&lt;Extension&lt;IToolWindow&gt;&gt;()</c>.
    /// </summary>
    public IReadOnlyList<Extension> Extensions { get; }
}

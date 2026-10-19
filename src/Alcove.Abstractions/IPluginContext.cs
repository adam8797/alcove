using Microsoft.Extensions.Logging;
using NuGet.Versioning;

namespace Alcove;

/// <summary>
/// What the host gives a plugin: its identity, a private data directory, a logger and the host's services.
/// </summary>
public interface IPluginContext
{
    /// <summary>The plugin's ID (its NuGet package ID).</summary>
    string PluginId { get; }

    /// <summary>The installed version of the plugin package.</summary>
    NuGetVersion Version { get; }

    /// <summary>A directory private to this plugin, under the plugin root. It survives updates.</summary>
    string DataDirectory { get; }

    /// <summary>A logger for this plugin, created from the host's logger factory.</summary>
    ILogger Logger { get; }

    /// <summary>The host's services, plus the plugin's cell services when a dependency injection add-on is used.</summary>
    IServiceProvider Services { get; }
}

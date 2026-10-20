using Microsoft.Extensions.Logging;
using NuGet.Versioning;

namespace Alcove.Loading;

/// <summary>The <see cref="IPluginContext"/> a plugin receives. Its data directory is created on first access.</summary>
internal sealed class PluginContext(string pluginId, NuGetVersion version, string dataDirectory, ILogger logger, IServiceProvider services) : IPluginContext
{
    private int _created;

    public string PluginId { get; } = pluginId;

    public NuGetVersion Version { get; } = version;

    public string DataDirectory
    {
        get
        {
            if (Volatile.Read(ref _created) == 0)
            {
                Directory.CreateDirectory(dataDirectory);
                Volatile.Write(ref _created, 1);
            }

            return dataDirectory;
        }
    }

    public ILogger Logger { get; } = logger;

    public IServiceProvider Services { get; } = services;
}

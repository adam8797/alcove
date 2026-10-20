using Microsoft.Extensions.Logging;

namespace Alcove.Catalog;

/// <summary>Catalog log messages. Event IDs 2100–2199.</summary>
internal static partial class CatalogLog
{
    [LoggerMessage(EventId = 2100, Level = LogLevel.Debug, Message = "Added {Count} extension(s) of plugin {PluginId} to the catalog")]
    public static partial void ExtensionsAdded(ILogger logger, int count, string pluginId);

    [LoggerMessage(EventId = 2101, Level = LogLevel.Error, Message = "An Observe<{ExtensionPoint}> callback threw for an extension of plugin {PluginId}")]
    public static partial void ObserverFailed(ILogger logger, Exception exception, string extensionPoint, string pluginId);

    [LoggerMessage(EventId = 2102, Level = LogLevel.Error, Message = "An ExtensionsAdded handler threw for plugin {PluginId}")]
    public static partial void HandlerFailed(ILogger logger, Exception exception, string pluginId);
}

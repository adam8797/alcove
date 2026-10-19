using Microsoft.Extensions.Logging;
using NuGet.Versioning;

namespace Alcove.Internal.Loading;

/// <summary>Plugin host log messages. Event IDs 2000–2099.</summary>
internal static partial class HostLog
{
    [LoggerMessage(EventId = 2000, Level = LogLevel.Information, Message = "Loading {Count} plugin(s) from '{Root}'")]
    public static partial void Loading(ILogger logger, int count, string root);

    [LoggerMessage(EventId = 2001, Level = LogLevel.Information, Message = "Loaded plugin {PluginId} {Version} in cell '{Cell}' with {ExtensionCount} extension(s)")]
    public static partial void Loaded(ILogger logger, string pluginId, NuGetVersion version, string cell, int extensionCount);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Error, Message = "Plugin {PluginId} faulted while {Stage}")]
    public static partial void Faulted(ILogger logger, Exception exception, string pluginId, string stage);

    [LoggerMessage(EventId = 2003, Level = LogLevel.Warning, Message = "Plugin {PluginId} isn't loaded: {Reason}")]
    public static partial void NotLoaded(ILogger logger, string pluginId, string reason);

    [LoggerMessage(EventId = 2004, Level = LogLevel.Error, Message = "A PluginFaulted handler threw for plugin {PluginId}")]
    public static partial void FaultHandlerFailed(ILogger logger, Exception exception, string pluginId);

    [LoggerMessage(EventId = 2005, Level = LogLevel.Warning, Message = "Disposing {What} of plugin {PluginId} threw")]
    public static partial void DisposeFailed(ILogger logger, Exception exception, string what, string pluginId);

    [LoggerMessage(EventId = 2006, Level = LogLevel.Information, Message = "Plugins loaded: {Loaded} loaded, {Faulted} faulted, {NotLoaded} not loaded, {Disabled} disabled")]
    public static partial void LoadFinished(ILogger logger, int loaded, int faulted, int notLoaded, int disabled);

    [LoggerMessage(EventId = 2007, Level = LogLevel.Debug, Message = "Plugin {PluginId} faulted again while {Stage}; keeping the first fault")]
    public static partial void FaultedAgain(ILogger logger, Exception exception, string pluginId, string stage);

    [LoggerMessage(EventId = 2008, Level = LogLevel.Warning, Message = "A callback registered on a plugin's cancellation token threw while the host was being disposed")]
    public static partial void CancellationCallbackFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 2009, Level = LogLevel.Error, Message = "The cell observer {Observer} threw in {What} for cell '{Cell}'")]
    public static partial void CellObserverFailed(ILogger logger, Exception exception, string observer, string what, string cell);

    [LoggerMessage(EventId = 2010, Level = LogLevel.Warning, Message = "Disposing the cell observer {Observer} threw")]
    public static partial void CellObserverDisposeFailed(ILogger logger, Exception exception, string observer);
}

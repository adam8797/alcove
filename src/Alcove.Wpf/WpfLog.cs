using Microsoft.Extensions.Logging;

namespace Alcove.Wpf;

/// <summary>Alcove.Wpf log messages (category <c>Alcove.Wpf</c>). Event IDs 6000–6099.</summary>
internal static partial class WpfLog
{
    [LoggerMessage(EventId = 6000, Level = LogLevel.Debug, Message = "Pre-loaded {Count} WPF-relevant assembly(ies) into cell '{Cell}': {Assemblies}")]
    public static partial void Preloaded(ILogger logger, int count, string cell, string assemblies);

    [LoggerMessage(EventId = 6001, Level = LogLevel.Warning, Message = "Couldn't pre-load the WPF-relevant assembly {Assembly} into cell '{Cell}': {Reason}")]
    public static partial void PreloadFailed(ILogger logger, string assembly, string cell, string reason);

    [LoggerMessage(EventId = 6002, Level = LogLevel.Debug, Message = "Forwarded the default context's failed load of {Assembly} to cell '{Cell}', which gave {Version}")]
    public static partial void Forwarded(ILogger logger, string assembly, string cell, Version? version);

    [LoggerMessage(EventId = 6003, Level = LogLevel.Warning, Message = "Didn't forward the default context's failed load of {Assembly}: several cells own a private copy ({Copies})")]
    public static partial void ForwardAmbiguous(ILogger logger, string assembly, string copies);

    [LoggerMessage(EventId = 6004, Level = LogLevel.Warning, Message = "Forwarding the default context's failed load of {Assembly} to cell '{Cell}' failed: {Reason}")]
    public static partial void ForwardFailed(ILogger logger, string assembly, string cell, string reason);

    [LoggerMessage(EventId = 6005, Level = LogLevel.Debug, Message = "The default context forwarder was {Action}")]
    public static partial void Forwarder(ILogger logger, string action);

    [LoggerMessage(EventId = 6006, Level = LogLevel.Warning, Message = "Plugin {PluginId} has resources to merge, but there is no Application and WpfPluginOptions.Resources isn't set")]
    public static partial void NoResourceTarget(ILogger logger, string pluginId);

    [LoggerMessage(EventId = 6007, Level = LogLevel.Debug, Message = "Merged {Count} resource dictionary(ies) of plugin {PluginId}: {Sources}")]
    public static partial void Merged(ILogger logger, int count, string pluginId, string sources);

    [LoggerMessage(EventId = 6008, Level = LogLevel.Warning, Message = "Removing the merged resource dictionaries of plugin {PluginId} failed")]
    public static partial void UnmergeFailed(ILogger logger, Exception exception, string pluginId);

    [LoggerMessage(EventId = 6009, Level = LogLevel.Information, Message = "The AppContext switch {Switch} isn't set: XAML referring to its own assembly's dictionaries by unversioned URIs may reach another cell's copy of that assembly. Set it in the host's project file (RuntimeHostConfigurationOption)")]
    public static partial void SwitchNotSet(ILogger logger, string @switch);

    [LoggerMessage(EventId = 6010, Level = LogLevel.Debug, Message = "Mapped {Count} view-model(s) of plugin {PluginId} to views: {Pairs}")]
    public static partial void ViewsMapped(ILogger logger, int count, string pluginId, string pairs);
}

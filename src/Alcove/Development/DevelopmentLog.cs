using Microsoft.Extensions.Logging;
using NuGet.Versioning;

namespace Alcove.Development;

/// <summary>Development plugin log messages. Event IDs 2400–2499.</summary>
internal static partial class DevelopmentLog
{
    [LoggerMessage(EventId = 2400, Level = LogLevel.Information, Message = "Development plugin {PluginId} {Version} loads from its build output '{Directory}', not from the plugin root")]
    public static partial void Planned(ILogger logger, string pluginId, NuGetVersion version, string directory);

    [LoggerMessage(EventId = 2401, Level = LogLevel.Debug, Message = "Development plugin {PluginId}: {PackageId} {Version} is {Classification}")]
    public static partial void Classified(ILogger logger, string pluginId, string packageId, NuGetVersion version, string classification);
}

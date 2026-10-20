using Alcove.Install;
using Microsoft.Extensions.Logging;
using NuGet.Versioning;

namespace Alcove.Feeds;

/// <summary>Feed, resolution and install log messages. Event IDs 3000–3099.</summary>
internal static partial class FeedLog
{
    [LoggerMessage(EventId = 3000, Level = LogLevel.Debug, Message = "NuGet: {Message}")]
    public static partial void NuGetDebug(ILogger logger, string message);

    [LoggerMessage(EventId = 3001, Level = LogLevel.Information, Message = "NuGet: {Message}")]
    public static partial void NuGetInformation(ILogger logger, string message);

    [LoggerMessage(EventId = 3002, Level = LogLevel.Warning, Message = "NuGet {Code}: {Message}")]
    public static partial void NuGetWarning(ILogger logger, string code, string message);

    // NuGet's errors (package not found, a feed down) become InstallResult/ChangeResult failures the app handles.
    [LoggerMessage(EventId = 3003, Level = LogLevel.Warning, Message = "NuGet {Code}: {Message}")]
    public static partial void NuGetError(ILogger logger, string code, string message);

    [LoggerMessage(EventId = 3004, Level = LogLevel.Warning, Message = "The feed '{Source}' is not allowed by the trust policy and is skipped: {Reason}")]
    public static partial void FeedNotAllowed(ILogger logger, string source, string reason);

    [LoggerMessage(EventId = 3005, Level = LogLevel.Warning, Message = "The feed '{Source}' failed: {Error}")]
    public static partial void FeedFailed(ILogger logger, string source, string error);

    [LoggerMessage(EventId = 3010, Level = LogLevel.Debug, Message = "Resolving cell '{Cell}' ({Plugins}): walk {Walk}")]
    public static partial void Walking(ILogger logger, string cell, string plugins, int walk);

    [LoggerMessage(EventId = 3011, Level = LogLevel.Information, Message = "Cell '{Cell}': the host's {PackageId} {HostVersion} doesn't satisfy {Range} (needed by {NeededBy}); it becomes cell-private")]
    public static partial void HostSharedUnpinned(ILogger logger, string cell, string packageId, NuGetVersion hostVersion, string range, string neededBy);

    [LoggerMessage(EventId = 3012, Level = LogLevel.Warning, Message = "Cell '{Cell}': {PackageId} is requested with different ranges ({Ranges}); {Version} satisfies them")]
    public static partial void Diamond(ILogger logger, string cell, string packageId, string ranges, NuGetVersion version);

    [LoggerMessage(EventId = 3020, Level = LogLevel.Information, Message = "Installing {PackageId} {Version} from '{Source}'")]
    public static partial void Installing(ILogger logger, string packageId, NuGetVersion version, string source);

    [LoggerMessage(EventId = 3021, Level = LogLevel.Information, Message = "Install of {PackageId}: {Status}{Reason}")]
    public static partial void InstallFinished(ILogger logger, string packageId, InstallStatus status, string reason);

    [LoggerMessage(EventId = 3022, Level = LogLevel.Warning, Message = "Trust check of {PackageId} {Version}: [{Code}] {Message}")]
    public static partial void TrustWarning(ILogger logger, string packageId, NuGetVersion version, string code, string message);
}

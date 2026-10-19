using Microsoft.Extensions.Logging;

namespace Alcove.Internal.Lifecycle;

/// <summary>Lifecycle log messages: staged changes, startup apply, cleanup, restore, verify. Event IDs 5000–5099.</summary>
internal static partial class LifecycleLog
{
    [LoggerMessage(EventId = 5000, Level = LogLevel.Information, Message = "Staged change for {PluginId}: {Result}")]
    public static partial void Staged(ILogger logger, string pluginId, string result);

    [LoggerMessage(EventId = 5001, Level = LogLevel.Information, Message = "Applied the staged changes in '{Root}': {Changes}")]
    public static partial void Applied(ILogger logger, string root, string changes);

    [LoggerMessage(EventId = 5002, Level = LogLevel.Warning, Message = "Staged changes in '{Root}' weren't applied, so the plugins load as they are; they apply at the next startup: {Reason}")]
    public static partial void ApplySkipped(ILogger logger, string root, string reason);

    [LoggerMessage(EventId = 5003, Level = LogLevel.Information, Message = "Deleted the unused package {PackageId} {Version}")]
    public static partial void PackageDeleted(ILogger logger, string packageId, string version);

    [LoggerMessage(EventId = 5004, Level = LogLevel.Warning, Message = "Couldn't delete the unused package folder '{Directory}' (retried at the next startup): {Reason}")]
    public static partial void DeleteFailed(ILogger logger, string directory, string reason);

    [LoggerMessage(EventId = 5005, Level = LogLevel.Debug, Message = "Skipped the package cleanup in '{Root}': another process holds the root lock")]
    public static partial void CleanupSkipped(ILogger logger, string root);

    [LoggerMessage(EventId = 5006, Level = LogLevel.Information, Message = "Restored {PackageId} {Version}")]
    public static partial void Restored(ILogger logger, string packageId, string version);

    [LoggerMessage(EventId = 5007, Level = LogLevel.Warning, Message = "Couldn't restore {PackageId} {Version}: {Reason}")]
    public static partial void RestoreFailed(ILogger logger, string packageId, string version, string reason);

    [LoggerMessage(EventId = 5008, Level = LogLevel.Warning, Message = "Verification of {PackageId}: {Issue}")]
    public static partial void VerificationIssue(ILogger logger, string packageId, string issue);

    [LoggerMessage(EventId = 5009, Level = LogLevel.Warning, Message = "The package cleanup in '{Root}' failed: {Reason}")]
    public static partial void CleanupFailed(ILogger logger, string root, string reason);
}

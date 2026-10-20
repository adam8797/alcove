using Microsoft.Extensions.Logging;

namespace Alcove.Trust;

/// <summary>Trust policy log messages. Event IDs 4000–4099.</summary>
internal static partial class TrustLog
{
    [LoggerMessage(EventId = 4000, Level = LogLevel.Debug, Message = "Trust store: {Message}")]
    public static partial void TrustStore(ILogger logger, string message);

    [LoggerMessage(EventId = 4001, Level = LogLevel.Warning, Message = "Trust store: {Problem}. Signatures from nuget.org and other public certificate authorities may be rejected as untrusted")]
    public static partial void TrustStoreProblem(ILogger logger, string problem);

    [LoggerMessage(EventId = 4002, Level = LogLevel.Error, Message = "The trust policy is invalid, so every feed and package is rejected: {Issue}")]
    public static partial void InvalidPolicy(ILogger logger, TrustIssue issue);

    [LoggerMessage(EventId = 4003, Level = LogLevel.Warning, Message = "Feed '{Source}' rejected by the trust policy: {Issues}")]
    public static partial void FeedRejected(ILogger logger, string source, string issues);

    [LoggerMessage(EventId = 4004, Level = LogLevel.Debug, Message = "Package {PackageId} {Version} accepted by the trust policy (signed: {IsSigned}){Issues}")]
    public static partial void PackageAccepted(ILogger logger, string packageId, string version, bool isSigned, string issues);

    [LoggerMessage(EventId = 4005, Level = LogLevel.Warning, Message = "Package {PackageId} {Version} rejected by the trust policy (signed: {IsSigned}): {Issues}")]
    public static partial void PackageRejected(ILogger logger, string packageId, string version, bool isSigned, string issues);
}

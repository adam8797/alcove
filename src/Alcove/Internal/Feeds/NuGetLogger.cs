using Microsoft.Extensions.Logging;
using NuGet.Common;
using NuGetLogLevel = NuGet.Common.LogLevel;

namespace Alcove.Internal.Feeds;

/// <summary>Forwards NuGet client log messages to the host's logger: NuGet's information level is chatty, so it goes to Debug.</summary>
internal sealed class NuGetLogger(Microsoft.Extensions.Logging.ILogger logger) : LoggerBase(NuGetLogLevel.Debug)
{
    public override void Log(ILogMessage message)
    {
        switch (message.Level)
        {
            case NuGetLogLevel.Error:
                FeedLog.NuGetError(logger, message.Code.ToString(), message.Message);
                break;
            case NuGetLogLevel.Warning:
                FeedLog.NuGetWarning(logger, message.Code.ToString(), message.Message);
                break;
            case NuGetLogLevel.Minimal:
                FeedLog.NuGetInformation(logger, message.Message);
                break;
            default:
                if (logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug))
                {
                    FeedLog.NuGetDebug(logger, message.Message);
                }

                break;
        }
    }

    public override Task LogAsync(ILogMessage message)
    {
        Log(message);
        return Task.CompletedTask;
    }
}

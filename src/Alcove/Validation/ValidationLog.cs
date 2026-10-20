using Microsoft.Extensions.Logging;

namespace Alcove.Validation;

/// <summary>Validation log messages. Event IDs 2200–2299.</summary>
internal static partial class ValidationLog
{
    // A validation error is an expected outcome (e.g. an incompatible plugin) reported in the ValidationReport, so
    // it logs as a warning: something for the app developer to act on, not a failure of the library or the app.
    [LoggerMessage(EventId = 2200, Level = LogLevel.Warning, Message = "Validation: {Issue}")]
    public static partial void Error(ILogger logger, ValidationIssue issue);

    [LoggerMessage(EventId = 2201, Level = LogLevel.Warning, Message = "Validation: {Issue}")]
    public static partial void Warning(ILogger logger, ValidationIssue issue);

    [LoggerMessage(EventId = 2202, Level = LogLevel.Information, Message = "Validated {PluginCount} installed plugin(s): {ErrorCount} error(s), {WarningCount} warning(s), {LoadableCount} loadable")]
    public static partial void Summary(ILogger logger, int pluginCount, int errorCount, int warningCount, int loadableCount);

    [LoggerMessage(EventId = 2203, Level = LogLevel.Debug, Message = "Validation: couldn't inspect '{Path}' for WPF metadata: {Error}")]
    public static partial void InspectFailed(ILogger logger, string path, string error);
}

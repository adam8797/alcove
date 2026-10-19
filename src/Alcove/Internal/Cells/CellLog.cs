using Microsoft.Extensions.Logging;

namespace Alcove.Internal.Cells;

/// <summary>Cell log messages. Event IDs 1000–1099.</summary>
internal static partial class CellLog
{
    [LoggerMessage(EventId = 1000, Level = LogLevel.Debug, Message = "Cell '{Cell}' created for {PluginCount} plugin(s)")]
    public static partial void CellCreated(ILogger logger, string cell, int pluginCount);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Trace, Message = "Cell '{Cell}': Load({Assembly} {Version}) -> {Answer}")]
    public static partial void Resolved(ILogger logger, string cell, string assembly, Version? version, CellAnswer answer);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Debug, Message = "Cell '{Cell}': {Assembly} {Version} is unknown to the cell and the host; leaving it to the default context")]
    public static partial void UnknownAssembly(ILogger logger, string cell, string assembly, Version? version);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Warning, Message = "Cell '{Cell}': the plan now says {Planned} for {Name}, but {Given} was already given; keeping {Given} (answers are frozen)")]
    public static partial void FrozenAnswerKept(ILogger logger, string cell, string name, CellAnswer planned, CellAnswer given);

    [LoggerMessage(EventId = 1004, Level = LogLevel.Error, Message = "Cell '{Cell}': {Assembly} {Requested} was requested, but the cell resolves it to {Actual} ({Answer}); rejecting the lower version")]
    public static partial void VersionTooLow(ILogger logger, string cell, string assembly, Version requested, Version? actual, CellAnswer answer);

    [LoggerMessage(EventId = 1005, Level = LogLevel.Debug, Message = "Cell '{Cell}': resolving {Name} failed: {Error}")]
    public static partial void ResolutionFailed(ILogger logger, string cell, string name, string error);

    [LoggerMessage(EventId = 1006, Level = LogLevel.Debug, Message = "Cell '{Cell}': reusing the WPF-relevant assembly {Assembly} loaded by cell '{OwnerCell}'")]
    public static partial void SharedAssemblyReused(ILogger logger, string cell, string assembly, string ownerCell);

    [LoggerMessage(EventId = 1007, Level = LogLevel.Trace, Message = "Cell '{Cell}': LoadUnmanagedDll({Name}) -> {Answer}")]
    public static partial void NativeResolved(ILogger logger, string cell, string name, CellAnswer answer);

    [LoggerMessage(EventId = 1008, Level = LogLevel.Warning, Message = "Native library '{FileName}' is loaded from '{Path}' in cell '{Cell}' and from '{OtherPath}' in cell '{OtherCell}'. Native libraries' own dependencies are matched by file name across the whole process, so the cells may end up sharing one of them")]
    public static partial void NativeCollision(ILogger logger, string fileName, string path, string cell, string otherPath, string otherCell);
}

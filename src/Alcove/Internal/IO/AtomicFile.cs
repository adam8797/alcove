namespace Alcove.Internal.IO;

/// <summary>Writes files so readers see either the old or the new content, never a partial file.</summary>
/// <remarks>
/// Readers open files with <see cref="ReadShare"/> (<see cref="ReadAllBytes"/>). On Linux and macOS a writer can
/// always rename over a file being read. On Windows the rename is refused while any reader has the file open (even
/// one sharing deletion), so the rename is retried for about a second: Alcove's own reads take milliseconds, and
/// other readers (e.g. an antivirus scanner) are usually as brief.
/// </remarks>
internal static class AtomicFile
{
    /// <summary>
    /// How readers share the files written here: anyone may write, delete or rename over them meanwhile (on Windows,
    /// a reader without <see cref="FileShare.Delete"/> makes the writer's rename fail).
    /// </summary>
    public const FileShare ReadShare = FileShare.ReadWrite | FileShare.Delete;

    /// <summary>How often a rename that hits a sharing violation is tried in all.</summary>
    internal const int MoveAttempts = 10;

    /// <summary>Writes <paramref name="content"/> to a temporary file next to <paramref name="path"/>, flushes it to disk, then renames it over the target.</summary>
    public static void WriteAllBytes(string path, ReadOnlySpan<byte> content)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 1, FileOptions.WriteThrough))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }

            MoveWithRetry(temp, fullPath, static (source, target) => File.Move(source, target, overwrite: true), Thread.Sleep);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>Reads a file written by <see cref="WriteAllBytes"/>, sharing it as <see cref="ReadShare"/> says.</summary>
    /// <exception cref="FileNotFoundException">The file doesn't exist.</exception>
    /// <exception cref="DirectoryNotFoundException">Its directory doesn't exist.</exception>
    public static byte[] ReadAllBytes(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, ReadShare, bufferSize: 1);
        using var content = new MemoryStream();
        stream.CopyTo(content);
        return content.ToArray();
    }

    /// <summary>
    /// Renames <paramref name="source"/> over <paramref name="target"/>, retrying a few times (with a growing pause)
    /// while the target is held by a reader that doesn't share deletion.
    /// </summary>
    internal static void MoveWithRetry(string source, string target, Action<string, string> move, Action<TimeSpan> pause)
    {
        var delay = TimeSpan.FromMilliseconds(10);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                move(source, target);
                return;
            }
            catch (Exception ex) when (attempt < MoveAttempts && IsSharingViolation(ex))
            {
                pause(delay);
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 200));
            }
        }
    }

    /// <summary>
    /// Whether a failed rename looks like another process holding the target open: <c>ERROR_SHARING_VIOLATION</c> or
    /// <c>ERROR_LOCK_VIOLATION</c>, or on Windows <c>ERROR_ACCESS_DENIED</c> (what replacing a file open without
    /// <see cref="FileShare.Delete"/> reports).
    /// </summary>
    internal static bool IsSharingViolation(Exception exception) => exception switch
    {
        UnauthorizedAccessException => OperatingSystem.IsWindows(),
        IOException io => (io.HResult & 0xFFFF) is 32 or 33,
        _ => false,
    };

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a stray temp file is harmless.
        }
    }
}

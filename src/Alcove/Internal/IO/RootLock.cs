using System.Diagnostics;

namespace Alcove.Internal.IO;

/// <summary>
/// The root lock: <c>&lt;root&gt;/.lock</c>, held open with <see cref="FileShare.None"/> while the state file
/// changes. It works across processes (an advisory <c>flock</c> on Unix, a sharing violation on Windows) and
/// across handles in one process. Loading needs no lock.
/// </summary>
internal sealed class RootLock : IDisposable
{
    /// <summary>The lock file name in the plugin root.</summary>
    public const string FileName = ".lock";

    /// <summary>How long <see cref="AcquireAsync"/> waits by default.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(50);

    private FileStream? _stream;

    private RootLock(string root, FileStream stream)
    {
        Root = root;
        _stream = stream;
    }

    /// <summary>The plugin root this lock protects.</summary>
    public string Root { get; }

    /// <summary>Whether the lock is still held.</summary>
    public bool IsHeld => _stream is not null;

    /// <summary>Takes the lock, retrying until <paramref name="timeout"/> expires.</summary>
    /// <exception cref="TimeoutException">Another process or handle held the lock for the whole timeout.</exception>
    public static async Task<RootLock> AcquireAsync(string root, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var fullRoot = Path.GetFullPath(root);
        Directory.CreateDirectory(fullRoot);
        var path = Path.Combine(fullRoot, FileName);
        var limit = timeout ?? DefaultTimeout;
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryOpen(path, out var stream))
            {
                return new RootLock(fullRoot, stream);
            }

            if (stopwatch.Elapsed >= limit)
            {
                throw new TimeoutException(
                    $"Couldn't take the root lock '{path}' within {limit.TotalSeconds:0.#} s: another process (or handle) is changing the plugin root.");
            }

            await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Takes the lock if it's free right now.</summary>
    public static RootLock? TryAcquire(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var fullRoot = Path.GetFullPath(root);
        Directory.CreateDirectory(fullRoot);
        if (!TryOpen(Path.Combine(fullRoot, FileName), out var stream))
        {
            return null;
        }

        return new RootLock(fullRoot, stream);
    }

    /// <summary>Throws unless this lock is held and protects <paramref name="root"/>.</summary>
    public void EnsureHeldFor(string root)
    {
        ObjectDisposedException.ThrowIf(_stream is null, this);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(Normalize(root), Normalize(Root), comparison))
        {
            throw new InvalidOperationException($"The root lock is for '{Root}', not '{root}'.");
        }
    }

    /// <summary>Releases the lock. The lock file stays (deleting it would race with the next owner).</summary>
    public void Dispose()
    {
        var stream = Interlocked.Exchange(ref _stream, null);
        stream?.Dispose();
    }

    private static bool TryOpen(string path, out FileStream stream)
    {
        try
        {
            stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1);
            return true;
        }
        catch (IOException)
        {
            // Sharing violation (Windows) or the lock held elsewhere (Unix flock).
            stream = null!;
            return false;
        }
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}

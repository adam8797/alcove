using Alcove.Storage;
using Microsoft.Extensions.Logging;
using NuGet.Versioning;

namespace Alcove.Lifecycle;

/// <summary>
/// The two lifecycle steps of a startup (architecture §6): applying staged changes before validation, and deleting
/// unused packages after loading.
/// </summary>
internal sealed class StartupMaintenance(PackageLayout layout, int versionsToKeep, ILogger logger)
{
    /// <summary>The folder unused packages are moved to before they're deleted (never a package ID: IDs can't start with a dot).</summary>
    public const string DeletedFolderName = ".deleted";

    /// <summary>
    /// Applies staged changes (switch versions, drop uninstalled plugins, flip enabled flags) and trims each plugin's
    /// previous versions to <c>VersionsToKeep</c>, under the root lock. Nothing is deleted here. If the lock can't be
    /// taken in time, or the state file can't be read or written, the plugins load as they are and the staged
    /// changes stay for the next startup.
    /// </summary>
    /// <param name="lockTimeout">How long to wait for the lock when there are staged changes.</param>
    /// <param name="toLoad">
    /// Called with the state the startup is about to load, while the root lock is held when possible, so the caller can
    /// write its in-use record before any other process's cleanup (which runs under the lock) could see the state.
    /// </param>
    /// <param name="cancellationToken">Cancels waiting for the lock.</param>
    public async Task ApplyStagedChangesAsync(TimeSpan lockTimeout, Action<StateDocument> toLoad, CancellationToken cancellationToken)
    {
        var store = new StateStore(layout.Root);
        StateDocument? read = null;
        try
        {
            read = store.Read();
            using var rootLock = StagedChanges.Apply(read, versionsToKeep) is null
                ? RootLock.TryAcquire(layout.Root)   // nothing to apply: don't wait for the lock just for the record
                : await RootLock.AcquireAsync(layout.Root, lockTimeout, cancellationToken).ConfigureAwait(false);
            if (rootLock is null)
            {
                // Another process holds the lock: record what this state references right away. A cleanup that ran
                // after that process changed the state, and before this record exists, could still delete a folder
                // this startup is about to load; the window is the time between the read above and this write.
                toLoad(read);
                return;
            }

            var state = store.Read();   // another process may have changed it meanwhile
            if (StagedChanges.Apply(state, versionsToKeep) is not { } applied)
            {
                toLoad(state);
                return;
            }

            store.Write(rootLock, applied);
            toLoad(applied);
            var described = StagedChanges.Describe(state);
            LifecycleLog.Applied(logger, layout.Root, described.Count == 0 ? "previous versions trimmed" : string.Join("; ", described));
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // An unreadable state file is validation's to report. On a timeout, record as early as possible (the same
            // window as above).
            if (ex is not InvalidDataException)
            {
                LifecycleLog.ApplySkipped(logger, layout.Root, ex.Message);
                if (read is not null)
                {
                    toLoad(read);
                }
            }
        }
    }

    /// <summary>
    /// Deletes every package folder that the state file doesn't refer to (as an active, staged or kept previous version,
    /// or a cell-private dependency of one), that this host doesn't use (<paramref name="inUse"/>) and that no live
    /// host's in-use record lists (<see cref="InUseRecords"/>). Best effort:
    /// skipped when another process holds the root lock; a folder that can't be deleted (Windows keeps loaded
    /// assemblies locked) stays and is tried again at the next startup.
    /// </summary>
    public void DeleteUnusedPackages(IEnumerable<(string Id, NuGetVersion Version)> inUse)
    {
        try
        {
            using var rootLock = RootLock.TryAcquire(layout.Root);
            if (rootLock is null)
            {
                LifecycleLog.CleanupSkipped(logger, layout.Root);
                return;
            }

            var state = new StateStore(layout.Root).Read();
            var keep = StagedChanges.ReferencedPackages(state)
                .Concat(inUse)
                .Select(p => Path.GetFullPath(layout.GetPackageDirectory(p.Id, p.Version)))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // What other running hosts (other processes, or other hosts of this one) use; stale records are removed.
            if (InUseRecords.ReadLive(layout, reason => LifecycleLog.CleanupFailed(logger, layout.Root, reason)) is not { } others)
            {
                return;
            }

            keep.UnionWith(others);
            if (!Directory.Exists(layout.PackagesDirectory))
            {
                return;
            }

            var trash = Path.Combine(layout.PackagesDirectory, DeletedFolderName);
            foreach (var idDirectory in Directory.EnumerateDirectories(layout.PackagesDirectory).ToList())
            {
                if (Path.GetFileName(idDirectory).StartsWith('.'))
                {
                    continue;
                }

                foreach (var versionDirectory in Directory.EnumerateDirectories(idDirectory).ToList())
                {
                    if (!NuGetVersion.TryParse(Path.GetFileName(versionDirectory), out var version) || keep.Contains(Path.GetFullPath(versionDirectory)))
                    {
                        continue;
                    }

                    if (MoveToTrash(versionDirectory, trash))
                    {
                        LifecycleLog.PackageDeleted(logger, Path.GetFileName(idDirectory), version.ToNormalizedString());
                    }
                }

                if (!Directory.EnumerateFileSystemEntries(idDirectory).Any())
                {
                    TryDelete(idDirectory);
                }
            }

            if (Directory.Exists(trash))
            {
                foreach (var deleted in Directory.EnumerateDirectories(trash).ToList())
                {
                    TryDelete(deleted);
                }

                if (!Directory.EnumerateFileSystemEntries(trash).Any())
                {
                    TryDelete(trash);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            LifecycleLog.CleanupFailed(logger, layout.Root, ex.Message);
        }
    }

    /// <summary>
    /// Moves a package folder out of the layout in one step (so it's never half there), then deletes it. A folder
    /// that can't be moved (a file in it is in use) stays where it is.
    /// </summary>
    private bool MoveToTrash(string directory, string trash)
    {
        var target = Path.Combine(trash, Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(trash);
            Directory.Move(directory, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LifecycleLog.DeleteFailed(logger, directory, ex.Message);
            return false;
        }

        TryDelete(target);
        return true;
    }

    private void TryDelete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LifecycleLog.DeleteFailed(logger, directory, ex.Message);
        }
    }
}

using System.Collections.Concurrent;
using Alcove.Feeds;
using Alcove.Install;
using Alcove.Lifecycle;
using Alcove.Loading;
using Alcove.Storage;
using Alcove.Trust;
using NuGet.Versioning;

namespace Alcove;

// Lifecycle (Phase 5): staged changes, applying them at startup, cleanup, pending changes, update checks, restore
// and verification. Everything here except InstallAsync's PendingRestart bookkeeping is restart-only: nothing loaded
// in this process changes (ADR 0005).
public sealed partial class PluginHost
{
    private readonly ConcurrentDictionary<string, IReadOnlyList<string>> _pendingRestart = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<(string Id, NuGetVersion Version)> _inUse = [];
    private LoadPlan? _loadedPlan;
    private string? _inUseRecord;   // guarded by _inUse

    /// <summary>How long state changes wait for another process's root lock (tests shorten it).</summary>
    internal TimeSpan LockTimeout { get; set; } = RootLock.DefaultTimeout;

    /// <summary>Stages an update of a plugin to the newest version it can use; see <see cref="UpdateAsync(string, VersionRange?, bool?, IProgress{InstallProgress}?, CancellationToken)"/>.</summary>
    /// <param name="pluginId">The plugin's ID.</param>
    /// <param name="cancellationToken">Cancels the update; nothing is staged once it's cancelled before the write.</param>
    /// <returns>What was staged, or why not.</returns>
    public Task<ChangeResult> UpdateAsync(string pluginId, CancellationToken cancellationToken) =>
        UpdateAsync(pluginId, null, null, null, cancellationToken);

    /// <summary>
    /// Stages an update of an installed plugin, applied at the next startup. The new version is resolved, downloaded,
    /// checked against the trust policy, extracted next to the old one and recorded <em>now</em>, so the next startup
    /// needs no network. Its cell (and the cells of the plugins depending on it) are resolved again, as an install
    /// does; at the next startup the whole cell reloads with the new version.
    /// </summary>
    /// <remarks>
    /// Without <paramref name="versionRange"/>, the newest version that this host can run and that the version ranges
    /// other plugins declare on this one accept. Nothing loaded in this process changes (ADR 0005). The previous
    /// version stays on disk for <see cref="RollbackAsync"/> (<see cref="PluginHostOptions.VersionsToKeep"/>).
    /// </remarks>
    /// <param name="pluginId">The plugin's ID.</param>
    /// <param name="versionRange">The versions to choose from (the highest usable one wins); <see langword="null"/> for any newer version.</param>
    /// <param name="includePrerelease">Whether prerelease versions are considered; <see langword="null"/> uses <see cref="PluginHostOptions.AllowPrerelease"/>. An installed prerelease, or a range naming one, always allows them.</param>
    /// <param name="progress">Receives the steps: resolving, downloading, verifying signatures, extracting, recording.</param>
    /// <param name="cancellationToken">Cancels the update; nothing is staged once it's cancelled before the write.</param>
    /// <returns>What was staged, or why not. Expected failures (not installed, nothing newer, incompatible, rejected by the trust policy, a feed error) are results.</returns>
    /// <exception cref="TimeoutException">Another process held the root lock for too long.</exception>
    /// <exception cref="InvalidOperationException">The <see cref="PluginHostOptions.NuGetConfigPath"/> file can't be read.</exception>
    public async Task<ChangeResult> UpdateAsync(
        string pluginId,
        VersionRange? versionRange = null,
        bool? includePrerelease = null,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ChangeResult result;
        try
        {
            result = await new PluginInstaller(_inventory, _layout, _options, _feeds.Value, Verifier, _trustVerdicts, _logger)
                .UpdateAsync(pluginId, versionRange, includePrerelease ?? _options.AllowPrerelease, LockTimeout, progress, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (FeedException.Find(ex) is { } feedError)
        {
            result = new ChangeResult(ChangeStatus.Failed, pluginId, null, [feedError.Message], [], []);
        }

        LifecycleLog.Staged(_logger, pluginId, result.ToString());
        return result;
    }

    /// <summary>
    /// Stages an uninstall, applied at the next startup: the plugin is removed from the state file and its packages are
    /// deleted after that startup, unless another plugin still uses them. Its data directory is kept. Blocked
    /// (<see cref="ChangeStatus.Blocked"/>) while other plugins depend on it, unless <paramref name="cascade"/> is set:
    /// then they're staged for uninstall too.
    /// </summary>
    /// <param name="pluginId">The plugin's ID.</param>
    /// <param name="cascade">Whether plugins depending on it (directly or not) are uninstalled with it.</param>
    /// <param name="cancellationToken">Cancels waiting for the root lock.</param>
    /// <returns>What was staged, or why not.</returns>
    /// <exception cref="TimeoutException">Another process held the root lock for too long.</exception>
    public Task<ChangeResult> UninstallAsync(string pluginId, bool cascade = false, CancellationToken cancellationToken = default) =>
        StageAsync(pluginId, stager => stager.UninstallAsync(pluginId, cascade, cancellationToken));

    /// <summary>Stages enabling a disabled plugin: it loads again from the next startup on.</summary>
    /// <param name="pluginId">The plugin's ID.</param>
    /// <param name="cancellationToken">Cancels waiting for the root lock.</param>
    /// <returns>What was staged, or why not; <see cref="ChangeResult.Issues"/> lists what validation expects at the next startup.</returns>
    /// <exception cref="TimeoutException">Another process held the root lock for too long.</exception>
    public Task<ChangeResult> EnableAsync(string pluginId, CancellationToken cancellationToken = default) =>
        StageAsync(pluginId, stager => stager.SetEnabledAsync(pluginId, enabled: true, cancellationToken));

    /// <summary>
    /// Stages disabling a plugin: it stays installed but isn't loaded from the next startup on. Plugins depending on it
    /// won't load either; <see cref="ChangeResult.Reasons"/> says so. It keeps running in this process.
    /// </summary>
    /// <param name="pluginId">The plugin's ID.</param>
    /// <param name="cancellationToken">Cancels waiting for the root lock.</param>
    /// <returns>What was staged, or why not.</returns>
    /// <exception cref="TimeoutException">Another process held the root lock for too long.</exception>
    public Task<ChangeResult> DisableAsync(string pluginId, CancellationToken cancellationToken = default) =>
        StageAsync(pluginId, stager => stager.SetEnabledAsync(pluginId, enabled: false, cancellationToken));

    /// <summary>
    /// Stages a switch back to the newest previous version kept on disk (from the next startup's point of view: a
    /// rollback right after <see cref="UpdateAsync(string, CancellationToken)"/> cancels the staged update). Fails
    /// clearly when no previous version is kept. The library never rolls back by itself (ADR 0011).
    /// </summary>
    /// <param name="pluginId">The plugin's ID.</param>
    /// <param name="cancellationToken">Cancels waiting for the root lock.</param>
    /// <returns>What was staged, or why not.</returns>
    /// <exception cref="TimeoutException">Another process held the root lock for too long.</exception>
    public Task<ChangeResult> RollbackAsync(string pluginId, CancellationToken cancellationToken = default) =>
        StageAsync(pluginId, stager => stager.RollbackAsync(pluginId, cancellationToken));

    /// <summary>
    /// What changes at the next startup: the state file on disk compared with what this process loaded. Lists plugins
    /// installed by another instance, staged updates, rollbacks, uninstalls, enables and disables (from any instance),
    /// changes another instance already applied at its startup, hot installs that are
    /// <see cref="InstallStatus.PendingRestart"/> (with their reasons), and plugins that didn't load for a missing plugin
    /// dependency that has been installed since (<see cref="PendingChangeKind.WillLoad"/>). Before
    /// <see cref="LoadAsync"/>, only staged changes. There's no file watcher: call it when you want to know.
    /// </summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The pending changes; empty when a restart changes nothing.</returns>
    public Task<IReadOnlyList<PendingChange>> GetPendingChangesAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<IReadOnlyList<PendingChange>>(cancellationToken);
        }

        try
        {
            var disk = new StateStore(_layout.Root).Read();
            return Task.FromResult(PendingChanges.Compute(disk, Volatile.Read(ref _loadedPlan), Plugins, _pendingRestart));
        }
        catch (Exception ex)
        {
            return Task.FromException<IReadOnlyList<PendingChange>>(ex);
        }
    }

    /// <summary>
    /// For each installed plugin, the newest version on the feeds that this host can run and that the version ranges
    /// other plugins declare on it accept; newer versions that can't be used are listed with the reason. Compared
    /// with the version the next startup loads. Reads package metadata only; a feed that fails is logged and skipped.
    /// </summary>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>One entry per installed plugin, in state file order.</returns>
    /// <exception cref="InvalidDataException">The state file can't be read.</exception>
    /// <exception cref="InvalidOperationException">The <see cref="PluginHostOptions.NuGetConfigPath"/> file can't be read.</exception>
    public async Task<IReadOnlyList<PluginUpdate>> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var state = new StateStore(_layout.Root).Read();
        return await new UpdateChecker(_inventory, _options, _feeds.Value, Verifier, _logger)
            .CheckAsync(state, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Restores the packages the state file pins; see <see cref="RestoreAsync(IProgress{InstallProgress}?, CancellationToken)"/>.</summary>
    /// <param name="cancellationToken">Cancels the restore.</param>
    /// <returns>What was restored, and what couldn't be.</returns>
    public Task<RestoreResult> RestoreAsync(CancellationToken cancellationToken) => RestoreAsync(null, cancellationToken);

    /// <summary>
    /// Downloads every package the state file pins (exact version and <c>contentHash</c>; active, staged and kept
    /// versions and their cell-private dependencies) that's missing from the plugin root, e.g. on a fresh machine with
    /// only the state file copied over. Each is checked against the trust policy and its pinned <c>contentHash</c>
    /// before extraction. It resolves nothing and changes no version. Call it before <see cref="LoadAsync"/>.
    /// </summary>
    /// <param name="progress">Receives the steps per package: downloading, verifying signatures, extracting; then recording.</param>
    /// <param name="cancellationToken">Cancels the restore.</param>
    /// <returns>What was restored, and what couldn't be (with the reason). Expected failures are results, not exceptions.</returns>
    /// <exception cref="TimeoutException">Another process held the root lock for too long.</exception>
    /// <exception cref="InvalidOperationException">The <see cref="PluginHostOptions.NuGetConfigPath"/> file can't be read.</exception>
    public async Task<RestoreResult> RestoreAsync(IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return await new PackageRestorer(_inventory, _layout, _feeds.Value, Verifier, _logger)
            .RestoreAsync(LockTimeout, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Verifies the plugin root without the trust policy; see <see cref="VerifyAsync(bool, CancellationToken)"/>.</summary>
    /// <param name="cancellationToken">Cancels the verification.</param>
    /// <returns>The report.</returns>
    public Task<VerificationReport> VerifyAsync(CancellationToken cancellationToken) => VerifyAsync(false, cancellationToken);

    /// <summary>
    /// A full check of the plugin root, beyond startup validation's sizes and timestamps: for every package the state
    /// file refers to, the kept <c>.nupkg</c>'s <c>contentHash</c> against the pinned one, and every extracted file
    /// (SHA-512) against the package's content. Local only; it can take a while for large plugins.
    /// </summary>
    /// <param name="verifySignatures">
    /// Also apply the trust policy to each kept <c>.nupkg</c> again (signatures, signers, revocation). With
    /// <see cref="RevocationCheck.Online"/> that may use the network.
    /// </param>
    /// <param name="cancellationToken">Cancels the verification.</param>
    /// <returns>The report.</returns>
    /// <exception cref="InvalidDataException">The state file can't be read.</exception>
    public async Task<VerificationReport> VerifyAsync(bool verifySignatures = false, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var state = new StateStore(_layout.Root).Read();
        return await new PackageVerification(_layout, Verifier, _logger)
            .VerifyAsync(state, verifySignatures, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Startup, before validation: applies staged changes under the root lock (architecture §6). Before the lock is
    /// released, the in-use record lists what the state to load references (active versions and their cell-private
    /// dependencies), so no other process's cleanup can delete it before this startup loads it.
    /// </summary>
    private Task ApplyStagedChangesAsync(CancellationToken cancellationToken) =>
        new StartupMaintenance(_layout, _options.VersionsToKeep, _logger).ApplyStagedChangesAsync(
            LockTimeout,
            state => RememberInUse(state.Plugins.SelectMany(StagedChanges.Packages)),
            cancellationToken);

    /// <summary>
    /// Startup, after loading (even if plugins faulted), and tests: deletes packages nothing refers to any more, never
    /// one a plan of this process (startup or hot install) loaded from.
    /// </summary>
    internal void DeleteUnusedPackages()
    {
        List<(string, NuGetVersion)> inUse;
        lock (_inUse)
        {
            inUse = _inUse.ToList();
        }

        new StartupMaintenance(_layout, _options.VersionsToKeep, _logger).DeleteUnusedPackages(inUse);
    }

    /// <summary>
    /// Remembers what a plan loads from: the packages of every plugin in it (a superset of what its cells may load),
    /// so the cleanup never deletes them; and the startup plan, for <see cref="GetPendingChangesAsync"/>.
    /// </summary>
    private void RememberLoaded(LoadPlan plan, bool startup)
    {
        if (startup)
        {
            Volatile.Write(ref _loadedPlan, plan);
        }

        RememberInUse(plan.Plugins.SelectMany(p => StagedChanges.Packages(p.State)));
    }

    /// <summary>Adds packages to what this host uses and rewrites its in-use record (read by other processes' cleanups).</summary>
    private void RememberInUse(IEnumerable<(string Id, NuGetVersion Version)> packages)
    {
        lock (_inUse)
        {
            _inUse.UnionWith(packages.Select(p => (p.Id.ToLowerInvariant(), p.Version)));
            if (_disposed)
            {
                return;
            }

            _inUseRecord ??= InUseRecords.NewPath(_layout.Root);
            try
            {
                InUseRecords.Write(_inUseRecord, _inUse);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LifecycleLog.CleanupFailed(_logger, _layout.Root, $"The in-use record '{_inUseRecord}' can't be written: {ex.Message}");
            }
        }
    }

    /// <summary>On dispose: this host no longer uses any package.</summary>
    private void DeleteInUseRecord()
    {
        lock (_inUse)
        {
            if (_inUseRecord is not null)
            {
                InUseRecords.Delete(_inUseRecord);
                _inUseRecord = null;
            }
        }
    }

    /// <summary>This host's in-use record (tests).</summary>
    internal string? InUseRecordPath
    {
        get
        {
            lock (_inUse)
            {
                return _inUseRecord;
            }
        }
    }

    /// <summary>Remembers why hot-installed plugins couldn't load, for <see cref="GetPendingChangesAsync"/>.</summary>
    private void RecordPendingRestart(IEnumerable<string> pluginIds, IReadOnlyList<string> reasons)
    {
        foreach (var id in pluginIds)
        {
            _pendingRestart[id] = reasons.ToList();
        }
    }

    private async Task<ChangeResult> StageAsync(string pluginId, Func<ChangeStager, Task<ChangeResult>> stage)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        var result = await stage(new ChangeStager(_inventory, _layout, _options, LockTimeout, TimeProvider.System,
            (state, packages, issues, ct) => PluginInstaller.VerifyKeptAsync(Verifier, _trustVerdicts, _layout, _logger, state, packages, issues, null, ct))).ConfigureAwait(false);
        LifecycleLog.Staged(_logger, pluginId, result.ToString());
        return result;
    }
}

using Alcove.Internal.Hosting;
using Alcove.Internal.Install;
using Alcove.Internal.IO;
using Alcove.Internal.Loading;
using Alcove.Internal.State;
using Microsoft.Extensions.Logging.Abstractions;
using NuGet.Versioning;

namespace Alcove.Internal.Lifecycle;

/// <summary>
/// Stages uninstalls, enables, disables and rollbacks (ADR 0005): under the root lock, re-reads the state file,
/// works out the state the next startup should load, validates it against the one it would load otherwise, and
/// records the difference as staged changes. Nothing loaded changes. Updates are staged by
/// <see cref="PluginInstaller.UpdateAsync"/>, which shares the commit step.
/// </summary>
/// <remarks>
/// <c>verifyKept</c> applies the trust policy to the packages (already in the plugin root) that a rollback switches
/// to (see <see cref="Trust.TrustVerdicts"/>), with the source feeds the current state records, and returns why it
/// rejected one; <see langword="null"/>: not checked.
/// </remarks>
internal sealed class ChangeStager(
    HostInventory host,
    PackageLayout layout,
    PluginHostOptions options,
    TimeSpan lockTimeout,
    TimeProvider clock,
    Func<StateDocument, IEnumerable<(string Id, NuGetVersion Version)>, List<ValidationIssue>, CancellationToken, Task<string?>>? verifyKept = null)
{
    private static readonly string[] s_incompatibleCodes =
        [ValidationCodes.HostOwnedConflict, ValidationCodes.HostOwnedMissing, ValidationCodes.ManifestSchemaUnsupported];

    /// <summary>Stages an uninstall, blocked while other plugins depend on it unless <paramref name="cascade"/> is set.</summary>
    public Task<ChangeResult> UninstallAsync(string pluginId, bool cascade, CancellationToken cancellationToken) =>
        RunAsync(pluginId, cancellationToken, (current, effective, plugin) =>
        {
            var dependents = Dependents(effective, plugin.Id);
            if (dependents.Count > 0 && !cascade)
            {
                return Fail(ChangeStatus.Blocked, plugin,
                    [.. dependents.Select(d => $"{d.Id} depends on {plugin.Id}."), "Uninstall them first, or pass cascade: true to uninstall them too."]);
            }

            var removed = dependents.Select(d => d.Id).Append(plugin.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var desired = effective with { Plugins = effective.Plugins.Where(p => !removed.Contains(p.Id)).ToList() };
            return Commit(current, effective, desired,
                new StageRequest(plugin.Id, StagedChangeKind.Uninstall, cascade, $"Uninstalled with {plugin.Id} (cascade), which it depends on."),
                blockOnErrors: false, null);
        });

    /// <summary>Stages enabling or disabling a plugin. New validation errors (e.g. dependents that won't load) are reported, not blocking.</summary>
    public Task<ChangeResult> SetEnabledAsync(string pluginId, bool enabled, CancellationToken cancellationToken) =>
        RunAsync(pluginId, cancellationToken, (current, effective, plugin) =>
        {
            if (plugin.Enabled == enabled)
            {
                return Unchanged(plugin, $"{plugin.Id} is already {(enabled ? "enabled" : "disabled")}{(current.Find(plugin.Id)!.Enabled == enabled ? "" : " from the next startup on")}.");
            }

            var desired = PackageRecorder.Merge(effective, [plugin with { Enabled = enabled }]);
            return Commit(current, effective, desired, new StageRequest(plugin.Id, StagedChangeKind.Update), blockOnErrors: false, null);
        });

    /// <summary>Stages a switch back to the newest previous version kept on disk (relative to the next startup's version).</summary>
    public Task<ChangeResult> RollbackAsync(string pluginId, CancellationToken cancellationToken)
    {
        var issues = new List<ValidationIssue>();
        return RunAsync(pluginId, cancellationToken, (current, effective, plugin) =>
        {
            if (plugin.PreviousVersions.FirstOrDefault() is not { } previous)
            {
                return Fail(ChangeStatus.Failed, plugin,
                    [$"No previous version of {plugin.Id} is kept on disk (VersionsToKeep is {options.VersionsToKeep}), so it can't be rolled back."]);
            }

            var desired = PackageRecorder.Merge(effective, [StagedChanges.AsVersion(previous) with { Id = plugin.Id, Enabled = plugin.Enabled }]);
            var active = current.Find(plugin.Id)!;
            var note = active.Version == previous.Version ? $"The staged change of {plugin.Id} was cancelled; it stays at {previous.Version}." : null;
            return Commit(current, effective, desired, new StageRequest(plugin.Id, StagedChangeKind.Rollback), blockOnErrors: true, note, issues);
        },
        precheck: async (current, plugin) =>
        {
            // The kept version's packages against the current trust policy (it may have changed since they were installed).
            if (verifyKept is null || plugin.PreviousVersions.FirstOrDefault() is not { } previous || current.Find(plugin.Id)!.Version == previous.Version)
            {
                return null;
            }

            return await verifyKept(current, StagedChanges.Packages(previous with { Id = plugin.Id }), issues, cancellationToken).ConfigureAwait(false) is { } rejected
                ? new ChangeResult(ChangeStatus.Failed, plugin.Id, previous.Version, [rejected], [], issues)
                : null;
        });
    }

    /// <summary>
    /// Validates the desired next-startup state against the current one and, unless that adds errors and
    /// <paramref name="blockOnErrors"/> is set, returns the state file to write (staged changes) and the result.
    /// </summary>
    internal (ChangeResult Result, StateDocument? Write) Commit(
        StateDocument current,
        StateDocument effective,
        StateDocument desired,
        StageRequest request,
        bool blockOnErrors,
        string? note,
        IReadOnlyList<ValidationIssue>? extraIssues = null) =>
        Commit(host, layout, options, clock, current, effective, desired, request, blockOnErrors, note, extraIssues);

    /// <summary>The commit step, for callers without a stager (updates).</summary>
    internal static (ChangeResult Result, StateDocument? Write) Commit(
        HostInventory host,
        PackageLayout layout,
        PluginHostOptions options,
        TimeProvider clock,
        StateDocument current,
        StateDocument effective,
        StateDocument desired,
        StageRequest request,
        bool blockOnErrors,
        string? note,
        IReadOnlyList<ValidationIssue>? extraIssues = null)
    {
        var validator = new PluginValidator(host, layout, options.Cells, NullLoggerFactory.Instance);
        var before = validator.Validate(effective).Report;
        var after = validator.Validate(desired).Report;
        var added = HotLoadRule.Added(before, after);
        var issues = (extraIssues ?? []).Concat(added).ToList();
        var version = desired.Find(request.PluginId)?.Version;
        var errors = added.Where(i => i.Severity == ValidationSeverity.Error).ToList();
        if (blockOnErrors && errors.Count > 0)
        {
            var status = errors.Any(e => s_incompatibleCodes.Contains(e.Code)) ? ChangeStatus.Incompatible : ChangeStatus.Failed;
            return (new ChangeResult(status, request.PluginId, version, errors.Select(e => e.Message).ToList(), [], issues), null);
        }

        var staged = StagedChanges.Stage(current, desired, request, clock.GetUtcNow());
        var reasons = new List<string>();
        if (note is not null)
        {
            reasons.Add(note);
        }

        reasons.AddRange(errors.Select(e => $"At the next startup: {e.Message}"));
        return (new ChangeResult(ChangeStatus.Staged, request.PluginId, version, reasons, StagedChanges.Describe(staged, current), issues), staged);
    }

    /// <summary>Every plugin that depends on <paramref name="pluginId"/>, directly or not.</summary>
    internal static IReadOnlyList<PluginState> Dependents(StateDocument state, string pluginId)
    {
        var found = new List<PluginState>();
        var pending = new Queue<string>([pluginId]);
        while (pending.TryDequeue(out var id))
        {
            foreach (var plugin in state.Plugins.Where(p => !found.Contains(p) && !string.Equals(p.Id, pluginId, StringComparison.OrdinalIgnoreCase)))
            {
                if (plugin.Dependencies.Any(d => d.Classification == DependencyClass.Plugin && string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase)))
                {
                    found.Add(plugin);
                    pending.Enqueue(plugin.Id);
                }
            }
        }

        return found;
    }

    private async Task<ChangeResult> RunAsync(
        string pluginId,
        CancellationToken cancellationToken,
        Func<StateDocument, StateDocument, PluginState, (ChangeResult Result, StateDocument? Write)> change,
        Func<StateDocument, PluginState, Task<ChangeResult?>>? precheck = null)
    {
        using var rootLock = await RootLock.AcquireAsync(layout.Root, lockTimeout, cancellationToken).ConfigureAwait(false);
        var store = new StateStore(layout.Root);
        StateDocument current;
        try
        {
            current = store.Read();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return new ChangeResult(ChangeStatus.Failed, pluginId, null, [$"The state file can't be read: {ex.Message}"], [], []);
        }

        var effective = StagedChanges.Effective(current);
        if (effective.Find(pluginId) is not { } plugin)
        {
            return current.Find(pluginId) is { } staged
                ? new ChangeResult(ChangeStatus.Unchanged, staged.Id, null, [$"{staged.Id} is staged for uninstall at the next startup."], [], [])
                : new ChangeResult(ChangeStatus.Failed, pluginId, null, [$"{pluginId} isn't installed."], [], []);
        }

        if (precheck is not null && await precheck(current, plugin).ConfigureAwait(false) is { } failed)
        {
            return failed;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var (result, write) = change(current, effective, plugin);
        if (write is not null)
        {
            store.Write(rootLock, write);
        }

        return result;
    }

    private static (ChangeResult, StateDocument?) Fail(ChangeStatus status, PluginState plugin, IReadOnlyList<string> reasons) =>
        (new ChangeResult(status, plugin.Id, plugin.Version, reasons, [], []), null);

    private static (ChangeResult, StateDocument?) Unchanged(PluginState plugin, string reason) =>
        (new ChangeResult(ChangeStatus.Unchanged, plugin.Id, plugin.Version, [reason], [], []), null);
}

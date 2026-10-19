using System.Text.Json;
using Alcove.Internal.Json;
using Alcove.Internal.State;
using NuGet.Versioning;

namespace Alcove.Internal.Lifecycle;

/// <summary>What a staging call asked for, so <see cref="StagedChanges.Stage"/> can name the changes it records.</summary>
/// <param name="PluginId">The plugin the app asked about.</param>
/// <param name="Kind">For a version switch of that plugin: <see cref="StagedChangeKind.Update"/> or <see cref="StagedChangeKind.Rollback"/>.</param>
/// <param name="Cascade">For an uninstall: whether dependents are uninstalled with it.</param>
/// <param name="OthersReason">Why other plugins change too (e.g. "uninstalled with X", "resolved again for X's update").</param>
internal sealed record StageRequest(string PluginId, StagedChangeKind Kind, bool Cascade = false, string? OthersReason = null);

/// <summary>
/// Staged changes (ADR 0005, architecture §6): computing the state the next startup will load, applying it at
/// startup, and turning a desired next-startup state back into staged changes of the current state.
/// </summary>
/// <remarks>
/// A plugin's state entry stays its <em>active</em> version until the next startup. A version switch (update,
/// rollback, or a cell resolved again) is <see cref="StagedChange.Target"/>, a full entry whose packages are already
/// in the plugin root; an uninstall is a <see cref="StagedChangeKind.Uninstall"/>; enabling and disabling are
/// <see cref="PluginState.StagedEnabled"/>. Every staging call works on the <see cref="Effective(StateDocument)"/> state, so staged
/// changes compose (e.g. a rollback of a staged update cancels it).
/// </remarks>
internal static class StagedChanges
{
    /// <summary>An entry as a version record: without previous versions and staged changes, enabled.</summary>
    public static PluginState AsVersion(PluginState plugin) =>
        plugin with { PreviousVersions = [], StagedChange = null, StagedEnabled = null, Enabled = true };

    /// <summary>Whether two entries describe the same version record (version, hash, files, assemblies, dependencies, ...).</summary>
    public static bool SameVersion(PluginState a, PluginState b) =>
        string.Equals(a.Id, b.Id, StringComparison.OrdinalIgnoreCase)
        && JsonSerializer.SerializeToUtf8Bytes(AsVersion(a) with { Id = "" }, AlcoveJsonContext.Default.PluginState)
            .AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(AsVersion(b) with { Id = "" }, AlcoveJsonContext.Default.PluginState));

    /// <summary>Whether any plugin has a staged change.</summary>
    public static bool HasStagedChanges(StateDocument state) =>
        state.Plugins.Any(p => p.StagedChange is not null || p.StagedEnabled is not null);

    /// <summary>
    /// The state the next startup will load: staged changes applied, previous versions not trimmed. A switched plugin's
    /// old active version becomes its newest previous version.
    /// </summary>
    public static StateDocument Effective(StateDocument state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state with { Plugins = state.Plugins.Select(Effective).OfType<PluginState>().ToList() };
    }

    /// <summary>A plugin as the next startup will see it, or <see langword="null"/> if it's staged for uninstall.</summary>
    public static PluginState? Effective(PluginState plugin)
    {
        if (plugin.StagedChange?.Kind == StagedChangeKind.Uninstall)
        {
            return null;
        }

        var enabled = plugin.StagedEnabled ?? plugin.Enabled;
        if (plugin.StagedChange?.Target is not { } target)
        {
            return plugin with { Enabled = enabled, StagedChange = null, StagedEnabled = null };
        }

        var previous = plugin.Version == target.Version
            ? plugin.PreviousVersions   // the same version, resolved again: the old record isn't worth keeping
            : [AsVersion(plugin), .. plugin.PreviousVersions];
        return AsVersion(target) with
        {
            Id = plugin.Id,
            Enabled = enabled,
            PreviousVersions = previous.Where(p => p.Version != target.Version).DistinctBy(p => p.Version).ToList(),
        };
    }

    /// <summary>
    /// What a startup applies: <see cref="Effective(StateDocument)"/>, with each plugin's previous versions trimmed to
    /// <paramref name="versionsToKeep"/>. Returns <see langword="null"/> if nothing changes.
    /// </summary>
    public static StateDocument? Apply(StateDocument state, int versionsToKeep)
    {
        ArgumentNullException.ThrowIfNull(state);
        var changed = HasStagedChanges(state);
        var plugins = new List<PluginState>();
        foreach (var plugin in Effective(state).Plugins)
        {
            var kept = plugin.PreviousVersions.Take(versionsToKeep).ToList();
            changed |= kept.Count != plugin.PreviousVersions.Count;
            plugins.Add(plugin with { PreviousVersions = kept });
        }

        return changed ? state with { Plugins = plugins } : null;
    }

    /// <summary>
    /// The current state with staged changes that make the next startup load <paramref name="desired"/> (an effective
    /// state). Plugins missing from it are staged for uninstall; a different version record is a staged switch; a
    /// different enabled flag is a staged enable or disable. Plugins only in <paramref name="desired"/> are new
    /// installs and are added as they are.
    /// </summary>
    public static StateDocument Stage(StateDocument current, StateDocument desired, StageRequest request, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(request);
        var plugins = new List<PluginState>();
        foreach (var plugin in current.Plugins)
        {
            var requested = string.Equals(plugin.Id, request.PluginId, StringComparison.OrdinalIgnoreCase);
            var wanted = desired.Find(plugin.Id);
            if (wanted is null)
            {
                plugins.Add(plugin.StagedChange?.Kind == StagedChangeKind.Uninstall
                    ? plugin
                    : plugin with
                    {
                        StagedEnabled = null,
                        StagedChange = new StagedChange
                        {
                            Kind = StagedChangeKind.Uninstall,
                            Cascade = request.Cascade,
                            Reason = requested ? null : request.OthersReason,
                            StagedAt = now,
                        },
                    });
                continue;
            }

            StagedChange? switched;
            if (SameVersion(plugin, wanted))
            {
                switched = null;
            }
            else if (plugin.StagedChange?.Target is { } target && SameVersion(target, wanted))
            {
                switched = plugin.StagedChange;
            }
            else if (!requested && plugin.StagedChange is { Target: { } staged } existing && staged.Version == wanted.Version)
            {
                // Another plugin's change resolved this one's staged version again: same switch, new dependency set.
                switched = existing with { Target = AsVersion(wanted) };
            }
            else
            {
                switched = new StagedChange
                {
                    Kind = requested ? request.Kind : StagedChangeKind.Update,
                    TargetVersion = wanted.Version,
                    Target = AsVersion(wanted),
                    Reason = requested ? null : request.OthersReason,
                    StagedAt = now,
                };
            }

            plugins.Add(plugin with
            {
                StagedChange = switched,
                StagedEnabled = wanted.Enabled == plugin.Enabled ? null : wanted.Enabled,
            });
        }

        foreach (var added in desired.Plugins.Where(p => current.Find(p.Id) is null))
        {
            plugins.Add(AsVersion(added) with { Enabled = added.Enabled });
        }

        return current with { Plugins = plugins };
    }

    /// <summary>
    /// <see cref="Stage"/> for an install, which is hot: new plugins are added as they are, and a plugin whose cell was
    /// resolved again keeps its version and has no staged switch takes its new dependency set at once (as installs
    /// always did). A plugin with a staged switch gets its staged target resolved again instead.
    /// </summary>
    public static StateDocument StageInstall(StateDocument current, StateDocument desired, StageRequest request, DateTimeOffset now)
    {
        var staged = Stage(current, desired, request, now);
        return staged with
        {
            Plugins = staged.Plugins.Select(p =>
                current.Find(p.Id) is { StagedChange: null } before && p.StagedChange is { Target: { } target } && target.Version == before.Version
                    ? AsVersion(target) with { Id = p.Id, Enabled = p.Enabled, PreviousVersions = p.PreviousVersions, StagedEnabled = p.StagedEnabled }
                    : p).ToList(),
        };
    }

    /// <summary>
    /// The staged changes of <paramref name="state"/> (or, with <paramref name="before"/>, only those that differ from
    /// it, plus plugins added since), as the app sees them.
    /// </summary>
    public static IReadOnlyList<PendingChange> Describe(StateDocument state, StateDocument? before = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        var changes = new List<PendingChange>();
        foreach (var plugin in state.Plugins)
        {
            var old = before?.Find(plugin.Id);
            if (before is not null && old is null)
            {
                changes.Add(new PendingChange(PendingChangeKind.Install, plugin.Id, null, plugin.Version, ["Installed as a plugin dependency; it loads at the next startup."], IsStaged: false));
                continue;
            }

            if (old is null || !SameStagedChange(old.StagedChange, plugin.StagedChange))
            {
                if (plugin.StagedChange is { } change)
                {
                    changes.Add(Describe(plugin, change));
                }
            }

            if ((old is null || old.StagedEnabled != plugin.StagedEnabled) && plugin.StagedEnabled is { } enabled)
            {
                changes.Add(new PendingChange(enabled ? PendingChangeKind.Enable : PendingChangeKind.Disable, plugin.Id, plugin.Version,
                    Effective(plugin)?.Version, [enabled ? "Enabled from the next startup on." : "Disabled from the next startup on."], IsStaged: true));
            }
        }

        return changes;
    }

    /// <summary>One staged change as the app sees it.</summary>
    public static PendingChange Describe(PluginState plugin, StagedChange change)
    {
        var reasons = new List<string>();
        if (change.Reason is { } reason)
        {
            reasons.Add(reason);
        }

        switch (change.Kind)
        {
            case StagedChangeKind.Uninstall:
                reasons.Add("Uninstalled at the next startup.");
                return new PendingChange(PendingChangeKind.Uninstall, plugin.Id, plugin.Version, null, reasons, IsStaged: true);
            case StagedChangeKind.Rollback:
                reasons.Add($"Rolled back to {change.TargetVersion} at the next startup.");
                return new PendingChange(PendingChangeKind.Rollback, plugin.Id, plugin.Version, change.TargetVersion, reasons, IsStaged: true);
            default:
                reasons.Add(change.TargetVersion == plugin.Version
                    ? "Its dependencies change at the next startup."
                    : $"Updated to {change.TargetVersion} at the next startup.");
                return new PendingChange(PendingChangeKind.Update, plugin.Id, plugin.Version, change.TargetVersion, reasons, IsStaged: true);
        }
    }

    /// <summary>Every package folder a state refers to: active, staged and previous versions, and their cell-private dependencies.</summary>
    public static IEnumerable<(string Id, NuGetVersion Version)> ReferencedPackages(StateDocument state)
    {
        ArgumentNullException.ThrowIfNull(state);
        foreach (var plugin in state.Plugins)
        {
            foreach (var record in VersionRecords(plugin))
            {
                foreach (var package in Packages(record))
                {
                    yield return package;
                }
            }
        }
    }

    /// <summary>A plugin's version records: active, staged target, previous versions.</summary>
    public static IEnumerable<PluginState> VersionRecords(PluginState plugin)
    {
        yield return plugin;
        if (plugin.StagedChange?.Target is { } target)
        {
            yield return target;
        }

        foreach (var previous in plugin.PreviousVersions)
        {
            yield return previous;
        }
    }

    /// <summary>The package folders one version record uses: the plugin's own and its cell-private dependencies'.</summary>
    public static IEnumerable<(string Id, NuGetVersion Version)> Packages(PluginState record)
    {
        yield return (record.Id, record.Version);
        foreach (var dependency in record.Dependencies.Where(d => d.Classification == DependencyClass.CellPrivate))
        {
            yield return (dependency.Id, dependency.Version);
        }
    }

    private static bool SameStagedChange(StagedChange? a, StagedChange? b) =>
        (a, b) switch
        {
            (null, null) => true,
            (null, _) or (_, null) => false,
            _ => a.Kind == b.Kind && a.TargetVersion == b.TargetVersion && a.StagedAt == b.StagedAt,
        };
}

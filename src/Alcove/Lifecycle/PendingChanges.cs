using Alcove.Loading;
using Alcove.Storage;

namespace Alcove.Lifecycle;

/// <summary>
/// The difference between what a process loaded and the state file on disk (architecture §6, "Pending changes"):
/// plugins installed by others, staged changes, changes another instance already applied at its startup, hot installs
/// pending restart, and plugins whose missing plugin dependency has been installed since (ADR 0011 amendment).
/// </summary>
internal static class PendingChanges
{
    /// <summary>Computes the pending changes.</summary>
    /// <param name="disk">The state file as it is now.</param>
    /// <param name="loaded">What validation planned at startup, or <see langword="null"/> before <c>LoadAsync</c> (then only staged changes are reported).</param>
    /// <param name="running">The plugins as this process sees them (startup plus hot installs).</param>
    /// <param name="pendingRestart">Hot installs of this process that couldn't load, with their reasons.</param>
    public static IReadOnlyList<PendingChange> Compute(
        StateDocument disk,
        LoadPlan? loaded,
        IReadOnlyList<PluginInfo> running,
        IReadOnlyDictionary<string, IReadOnlyList<string>> pendingRestart)
    {
        ArgumentNullException.ThrowIfNull(disk);
        if (loaded is null)
        {
            return StagedChanges.Describe(disk);
        }

        var changes = new List<PendingChange>();
        var runningById = new Dictionary<string, PluginInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var plugin in running)
        {
            runningById.TryAdd(plugin.Id, plugin);
        }

        var atStartup = loaded.Plugins.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var next = StagedChanges.Effective(disk);
        foreach (var plugin in disk.Plugins)
        {
            var effective = next.Find(plugin.Id);
            if (!runningById.TryGetValue(plugin.Id, out var info))
            {
                changes.Add(new PendingChange(PendingChangeKind.Install, plugin.Id, null, effective?.Version ?? plugin.Version,
                    ["Installed, but not loaded in this process (e.g. by another instance); it loads at the next startup."], IsStaged: false));
                continue;
            }

            if (pendingRestart.TryGetValue(plugin.Id, out var reasons))
            {
                changes.Add(new PendingChange(PendingChangeKind.PendingRestart, plugin.Id, null, effective?.Version ?? plugin.Version, reasons, IsStaged: false));
            }
            else if (plugin.Version != info.Version)
            {
                changes.Add(new PendingChange(plugin.Version > info.Version ? PendingChangeKind.Update : PendingChangeKind.Rollback, plugin.Id, info.Version, plugin.Version,
                    [$"Another instance switched it to {plugin.Version} at its startup."], IsStaged: false));
            }

            if (info.Status == PluginStatus.Disabled && plugin.Enabled)
            {
                changes.Add(new PendingChange(PendingChangeKind.Enable, plugin.Id, info.Version, plugin.Version, ["Another instance enabled it at its startup."], IsStaged: false));
            }
            else if (info.Status != PluginStatus.Disabled && !plugin.Enabled && !pendingRestart.ContainsKey(plugin.Id))
            {
                changes.Add(new PendingChange(PendingChangeKind.Disable, plugin.Id, info.Version, plugin.Version, ["Another instance disabled it at its startup."], IsStaged: false));
            }

            foreach (var staged in StagedChanges.Describe(new StateDocument { Plugins = [plugin] }))
            {
                changes.Add(staged with { CurrentVersion = info.Version });
            }

            if (info.Status is PluginStatus.NotLoaded or PluginStatus.Faulted
                && !pendingRestart.ContainsKey(plugin.Id)
                && effective is { Enabled: true }
                && loaded.Plugins.FirstOrDefault(p => string.Equals(p.Id, plugin.Id, StringComparison.OrdinalIgnoreCase)) is { } planned)
            {
                var arrived = planned.PluginDependencies
                    .Where(d => !atStartup.Contains(d) && next.Find(d) is { Enabled: true })
                    .ToList();
                if (arrived.Count > 0)
                {
                    changes.Add(new PendingChange(PendingChangeKind.WillLoad, plugin.Id, info.Version, effective.Version,
                        [$"Its plugin dependency {string.Join(", ", arrived)} has been installed since startup; it loads at the next startup."], IsStaged: false));
                }
            }
        }

        foreach (var info in running.Where(p => disk.Find(p.Id) is null))
        {
            changes.Add(new PendingChange(PendingChangeKind.Uninstall, info.Id, info.Version, null, ["Another instance uninstalled it at its startup."], IsStaged: false));
        }

        return changes;
    }
}

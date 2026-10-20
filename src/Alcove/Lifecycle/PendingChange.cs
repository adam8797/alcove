using Alcove.Install;
using NuGet.Versioning;

namespace Alcove.Lifecycle;

/// <summary>
/// A difference between what this process loaded and the state file on disk: something that changes at the next
/// startup (<see cref="PluginHost.GetPendingChangesAsync"/>), or a change a staging call recorded (<see cref="ChangeResult.Changes"/>).
/// </summary>
/// <param name="Kind">What changes.</param>
/// <param name="PluginId">The plugin.</param>
/// <param name="CurrentVersion">The version loaded in (or known to) this process, or <see langword="null"/> if it has none.</param>
/// <param name="NewVersion">The version from the next startup on, or <see langword="null"/> if the plugin goes away.</param>
/// <param name="Reasons">A description, and why (e.g. the reasons a hot install is pending restart).</param>
/// <param name="IsStaged">
/// <see langword="true"/> for a staged change recorded in the state file; <see langword="false"/> when the state file
/// already says so (e.g. another instance installed the plugin, or applied a change at its startup).
/// </param>
public sealed record PendingChange(
    PendingChangeKind Kind,
    string PluginId,
    NuGetVersion? CurrentVersion,
    NuGetVersion? NewVersion,
    IReadOnlyList<string> Reasons,
    bool IsStaged)
{
    /// <summary><see cref="Reasons"/> as one message, or <see langword="null"/> if there are none (as in <see cref="ChangeResult.Reason"/>).</summary>
    public string? Reason => Reasons.Count == 0 ? null : string.Join(" ", Reasons);

    /// <inheritdoc />
    public override string ToString() =>
        $"{Kind} {PluginId} {CurrentVersion?.ToNormalizedString() ?? "-"} -> {NewVersion?.ToNormalizedString() ?? "-"}{(Reason is null ? "" : $": {Reason}")}";
}

/// <summary>The kind of a <see cref="PendingChange"/>.</summary>
public enum PendingChangeKind
{
    /// <summary>A plugin installed but not loaded in this process (e.g. by another instance); it loads at the next startup.</summary>
    Install,

    /// <summary>A switch to another version (or, with the same version, a dependency set resolved again).</summary>
    Update,

    /// <summary>A switch back to a previous version kept on disk.</summary>
    Rollback,

    /// <summary>The plugin goes away.</summary>
    Uninstall,

    /// <summary>The plugin loads again from the next startup on.</summary>
    Enable,

    /// <summary>The plugin stays installed but isn't loaded from the next startup on.</summary>
    Disable,

    /// <summary>A plugin this process installed but couldn't load (<see cref="InstallStatus.PendingRestart"/>); <see cref="PendingChange.Reasons"/> says why.</summary>
    PendingRestart,

    /// <summary>
    /// A plugin that didn't load at startup because a plugin it depends on was missing, and that plugin has been
    /// installed since. It isn't activated during the run (ADR 0011); it loads at the next startup.
    /// </summary>
    WillLoad,
}

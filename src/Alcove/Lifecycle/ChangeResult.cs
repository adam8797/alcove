using Alcove.Trust;
using Alcove.Validation;
using NuGet.Versioning;

namespace Alcove.Lifecycle;

/// <summary>
/// What a staged change (<see cref="PluginHost.UpdateAsync(string, CancellationToken)"/>, <see cref="PluginHost.UninstallAsync"/>,
/// <see cref="PluginHost.EnableAsync"/>, <see cref="PluginHost.DisableAsync"/>, <see cref="PluginHost.RollbackAsync"/>)
/// did. Nothing loaded changes in this process: staged changes apply at the next startup. Expected failures are
/// reported here, not thrown.
/// </summary>
public sealed class ChangeResult
{
    internal ChangeResult(
        ChangeStatus status,
        string pluginId,
        NuGetVersion? version,
        IReadOnlyList<string> reasons,
        IReadOnlyList<PendingChange> changes,
        IReadOnlyList<ValidationIssue> issues)
    {
        Status = status;
        PluginId = pluginId;
        Version = version;
        Reasons = reasons;
        Changes = changes;
        Issues = issues;
    }

    /// <summary>The outcome.</summary>
    public ChangeStatus Status { get; }

    /// <summary>The plugin the call was about.</summary>
    public string PluginId { get; }

    /// <summary>
    /// The plugin's version from the next startup on (an update's or rollback's target), or its current version;
    /// <see langword="null"/> if it isn't installed or will be uninstalled.
    /// </summary>
    public NuGetVersion? Version { get; }

    /// <summary>Why the status is what it is (e.g. the plugins blocking an uninstall), one message per cause.</summary>
    public IReadOnlyList<string> Reasons { get; }

    /// <summary><see cref="Reasons"/> as one message, or <see langword="null"/> if there are none.</summary>
    public string? Reason => Reasons.Count == 0 ? null : string.Join(" ", Reasons);

    /// <summary>
    /// What this call staged: the plugin's own change and the ones it brought along (dependents uninstalled with
    /// <c>cascade</c>, plugins of the same cell resolved again, new plugin dependencies of an update).
    /// </summary>
    public IReadOnlyList<PendingChange> Changes { get; }

    /// <summary>What validation of the next startup's state found that wasn't there before, and what the trust policy reported (<see cref="TrustIssueCodes"/>).</summary>
    public IReadOnlyList<ValidationIssue> Issues { get; }

    /// <summary>Whether the change was staged (it applies at the next startup).</summary>
    public bool IsStaged => Status == ChangeStatus.Staged;

    /// <inheritdoc />
    public override string ToString() =>
        $"{PluginId} {Version?.ToNormalizedString() ?? "(no version)"}: {Status}{(Reason is null ? "" : $" ({Reason})")}";
}

/// <summary>The outcome of a staged change.</summary>
public enum ChangeStatus
{
    /// <summary>Recorded in the state file; it applies at the next startup.</summary>
    Staged,

    /// <summary>Nothing to do: e.g. the plugin is already at the newest version, already disabled, or already staged for uninstall.</summary>
    Unchanged,

    /// <summary>Not staged: other plugins depend on the plugin (uninstall without <c>cascade</c>). <see cref="ChangeResult.Reasons"/> names them.</summary>
    Blocked,

    /// <summary>Not staged: the version found needs something this host can't provide, e.g. a newer version of the host's contracts.</summary>
    Incompatible,

    /// <summary>Not staged: the plugin isn't installed, no version can be used, a feed failed, the trust policy rejected a package, or validation of the new state failed.</summary>
    Failed,
}

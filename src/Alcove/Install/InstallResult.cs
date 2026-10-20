using Alcove.Trust;
using Alcove.Validation;
using NuGet.Versioning;

namespace Alcove.Install;

/// <summary>What <see cref="PluginHost.InstallAsync(string, NuGet.Versioning.VersionRange?, bool?, IProgress{InstallProgress}?, CancellationToken)"/> did. Expected failures are reported here, not thrown.</summary>
public sealed class InstallResult
{
    internal InstallResult(
        InstallStatus status,
        string packageId,
        NuGetVersion? version,
        IReadOnlyList<string> reasons,
        IReadOnlyList<string> conflictingNames,
        IReadOnlyList<PluginInfo> plugins,
        IReadOnlyList<ValidationIssue> issues)
    {
        Status = status;
        PackageId = packageId;
        Version = version;
        Reasons = reasons;
        ConflictingNames = conflictingNames;
        Plugins = plugins;
        Issues = issues;
    }

    /// <summary>The outcome.</summary>
    public InstallStatus Status { get; }

    /// <summary>The requested plugin's package ID.</summary>
    public string PackageId { get; }

    /// <summary>The version installed (or that was chosen), if one was found.</summary>
    public NuGetVersion? Version { get; }

    /// <summary>Why the status isn't <see cref="InstallStatus.Loaded"/>: one message per cause. Empty when loaded.</summary>
    public IReadOnlyList<string> Reasons { get; }

    /// <summary><see cref="Reasons"/> as one message, or <see langword="null"/> if there are none.</summary>
    public string? Reason => Reasons.Count == 0 ? null : string.Join(" ", Reasons);

    /// <summary>
    /// For <see cref="InstallStatus.PendingRestart"/>: the assembly (or native library) names whose answer in an
    /// already loaded cell would have to change.
    /// </summary>
    public IReadOnlyList<string> ConflictingNames { get; }

    /// <summary>
    /// The plugins installed: the requested one and any plugin dependencies installed with it (dependencies first),
    /// with their status in this host.
    /// </summary>
    public IReadOnlyList<PluginInfo> Plugins { get; }

    /// <summary>
    /// What validation of the new state found that wasn't there before (e.g. WPF duplicate warnings), and what the
    /// trust policy reported (with its own codes, <see cref="TrustIssueCodes"/>).
    /// </summary>
    public IReadOnlyList<ValidationIssue> Issues { get; }

    /// <summary>Whether the plugin is installed (any status but <see cref="InstallStatus.Incompatible"/> and <see cref="InstallStatus.Failed"/>).</summary>
    public bool IsInstalled => Status is not (InstallStatus.Incompatible or InstallStatus.Failed);

    /// <inheritdoc />
    public override string ToString() =>
        $"{PackageId} {Version?.ToNormalizedString() ?? "(no version)"}: {Status}{(Reason is null ? "" : $" ({Reason})")}";
}

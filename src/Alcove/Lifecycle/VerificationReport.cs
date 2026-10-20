using Alcove.Validation;

namespace Alcove.Lifecycle;

/// <summary>
/// The result of <see cref="PluginHost.VerifyAsync(bool, CancellationToken)"/>: a full re-hash of every package the state file refers to
/// against its pinned <c>contentHash</c>, and optionally the trust policy applied again to the kept <c>.nupkg</c> files.
/// </summary>
public sealed class VerificationReport
{
    internal VerificationReport(IReadOnlyList<ValidationIssue> issues, int packagesChecked, int filesChecked)
    {
        Issues = issues;
        PackagesChecked = packagesChecked;
        FilesChecked = filesChecked;
    }

    /// <summary>
    /// Every finding, e.g. <see cref="ValidationCodes.ContentHashMismatch"/>, <see cref="ValidationCodes.FileContentChanged"/>,
    /// <see cref="ValidationCodes.FileMissing"/>, or a trust policy code. <see cref="ValidationIssue.PluginId"/> is the package concerned.
    /// </summary>
    public IReadOnlyList<ValidationIssue> Issues { get; }

    /// <summary>How many package folders were checked.</summary>
    public int PackagesChecked { get; }

    /// <summary>How many extracted files were hashed.</summary>
    public int FilesChecked { get; }

    /// <summary>Whether there's at least one error (as in <see cref="ValidationReport.HasErrors"/>).</summary>
    public bool HasErrors => Issues.Any(i => i.Severity == ValidationSeverity.Error);

    /// <inheritdoc />
    public override string ToString() =>
        Issues.Count == 0
            ? $"{PackagesChecked} package(s), {FilesChecked} file(s): no issues."
            : string.Join(Environment.NewLine, Issues);
}

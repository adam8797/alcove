using NuGet.Versioning;

namespace Alcove.Trust;

/// <summary>Applies the app's <see cref="TrustPolicy"/> to feeds (before download) and to downloaded packages (before extraction).</summary>
internal interface IPackageVerifier
{
    /// <summary>
    /// Identifies the policy this verifier applies, for remembering its verdicts (<see cref="TrustVerdicts"/>). Two
    /// verifiers with the same fingerprint must decide alike. By default, this instance.
    /// </summary>
    string PolicyFingerprint => $"{GetType().FullName}#{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this)}";

    /// <summary>
    /// Whether an acceptance of a package may be remembered for the host's lifetime (<see cref="TrustVerdicts"/>), so a
    /// plan reusing the package doesn't verify it again. <see cref="PackageVerifier"/>: only with
    /// <see cref="RevocationCheck.Offline"/>. Otherwise it's remembered for <see cref="TrustVerdicts.OnlineAcceptanceLifetime"/>
    /// only, so a revocation is seen soon.
    /// </summary>
    bool RemembersAcceptances { get; }

    /// <summary>Feed check before download.</summary>
    /// <param name="source">The feed's source URL or folder path.</param>
    TrustDecision CheckFeed(string source);

    /// <summary>
    /// Feed check of a package already in the plugin root that a new plan reuses: the feed it was downloaded from,
    /// as recorded at install. Never cached.
    /// </summary>
    /// <param name="packageId">The package ID.</param>
    /// <param name="version">The package version.</param>
    /// <param name="source">The recorded source feed, or <see langword="null"/> when none is recorded.</param>
    TrustDecision CheckKeptSource(string packageId, NuGetVersion version, string? source);

    /// <summary>Signature and integrity check of a downloaded <c>.nupkg</c>, before extraction.</summary>
    /// <param name="nupkgPath">The downloaded package.</param>
    /// <param name="packageId">The package ID that was requested.</param>
    /// <param name="version">The version that was requested.</param>
    /// <param name="cancellationToken">Cancels the verification. Online revocation checks may not stop promptly.</param>
    Task<TrustDecision> VerifyPackageAsync(string nupkgPath, string packageId, NuGetVersion version, CancellationToken cancellationToken);
}

/// <summary>The outcome of a trust check.</summary>
/// <param name="Allowed">Whether the feed or package may be used.</param>
/// <param name="IsSigned">Whether the package carries a signature (always <see langword="false"/> for feed checks).</param>
/// <param name="Issues">Why, in our own words. A rejection always has at least one <see cref="TrustIssueSeverity.Error"/>.</param>
internal sealed record TrustDecision(bool Allowed, bool IsSigned, IReadOnlyList<TrustIssue> Issues);

/// <summary>One reason behind a <see cref="TrustDecision"/>.</summary>
/// <param name="Severity">How serious it is. Only errors reject.</param>
/// <param name="Code">One of <see cref="TrustIssueCodes"/>: stable, and ours (not NuGet's).</param>
/// <param name="Message">A readable explanation. NuGet's NU30xx code, when there is one, is kept in the text.</param>
internal sealed record TrustIssue(TrustIssueSeverity Severity, string Code, string Message)
{
    /// <inheritdoc/>
    public override string ToString() => $"{Severity} {Code}: {Message}";
}

/// <summary>How serious a <see cref="TrustIssue"/> is.</summary>
internal enum TrustIssueSeverity
{
    /// <summary>For information only.</summary>
    Info,

    /// <summary>Accepted, but worth attention.</summary>
    Warning,

    /// <summary>Rejected.</summary>
    Error,
}

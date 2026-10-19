namespace Alcove;

/// <summary>
/// The stable codes of trust policy issues: <see cref="ValidationIssue.Code"/> of the trust entries in
/// <see cref="InstallResult.Issues"/> and <see cref="ChangeResult.Issues"/>, and the <c>[Code]</c> in their reasons.
/// They are Alcove's own (not NuGet's); NuGet's NU30xx code, when there is one, is kept in the message.
/// </summary>
public static class TrustIssueCodes
{
    /// <summary>The feed isn't in <see cref="TrustPolicy.AllowedFeeds"/>.</summary>
    public const string FeedNotAllowed = "FeedNotAllowed";

    /// <summary>
    /// A package already in the plugin root that a new plan reuses has no recorded source feed, while
    /// <see cref="TrustPolicy.AllowedFeeds"/> isn't empty, so the policy can't be applied to it.
    /// </summary>
    public const string UnknownSource = "UnknownSource";

    /// <summary>The trust policy itself is invalid (e.g. a malformed fingerprint), so nothing is accepted.</summary>
    public const string InvalidPolicy = "InvalidPolicy";

    /// <summary>The package isn't signed (NU3004).</summary>
    public const string NotSigned = "NotSigned";

    /// <summary>The package changed since it was signed (NU3008).</summary>
    public const string Tampered = "Tampered";

    /// <summary>The package isn't signed by any of <see cref="TrustPolicy.AllowedSigners"/> (NU3034).</summary>
    public const string SignerNotAllowed = "SignerNotAllowed";

    /// <summary>A signing certificate doesn't chain to a trusted root, or its chain couldn't be built (NU3018).</summary>
    public const string UntrustedCertificate = "UntrustedCertificate";

    /// <summary>A certificate's revocation status couldn't be determined (NU3018, revocation).</summary>
    public const string RevocationUnknown = "RevocationUnknown";

    /// <summary>A signature has no timestamp, so it stops being valid when its certificate expires (NU3027).</summary>
    public const string NotTimestamped = "NotTimestamped";

    /// <summary>A timestamp is invalid or untrusted (NU3020–NU3029).</summary>
    public const string InvalidTimestamp = "InvalidTimestamp";

    /// <summary>A signing certificate has expired or isn't yet valid (NU3037, NU3036).</summary>
    public const string CertificateExpired = "CertificateExpired";

    /// <summary>Any other signature problem reported by NuGet.</summary>
    public const string InvalidSignature = "InvalidSignature";

    /// <summary>The package was rejected without a specific reason from NuGet; the message summarises what failed.</summary>
    public const string VerificationFailed = "VerificationFailed";

    /// <summary>The file isn't a readable package, or a package in the plugin root has lost its kept <c>.nupkg</c>.</summary>
    public const string Unreadable = "Unreadable";

    /// <summary>The package's ID or version isn't the one that was requested.</summary>
    public const string IdentityMismatch = "IdentityMismatch";

    /// <summary>The package is unsigned and accepted because signatures aren't required (information; not in results).</summary>
    public const string UnsignedAccepted = "UnsignedAccepted";
}

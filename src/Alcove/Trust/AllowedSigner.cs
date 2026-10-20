namespace Alcove.Trust;

/// <summary>A signer the <see cref="TrustPolicy"/> accepts: an author certificate, or a repository certificate with optional package owners.</summary>
public sealed record AllowedSigner
{
    /// <summary>Whether <see cref="Fingerprint"/> identifies an author certificate or a repository certificate.</summary>
    public SignerKind Kind { get; init; }

    /// <summary>The SHA-256 fingerprint (hex) of the leaf signing certificate. It changes when the signer renews the certificate.</summary>
    public string Fingerprint { get; init; } = "";

    /// <summary>
    /// Repository signers only: the package must list at least one of these owners in its repository signature.
    /// Empty: any owner.
    /// </summary>
    public IList<string> Owners { get; init; } = new List<string>();

    /// <summary>
    /// Accept this certificate even if it doesn't chain to a trusted root (a self-signed or private-CA certificate).
    /// No certificate store is changed.
    /// </summary>
    public bool AllowUntrustedRoot { get; init; }

    /// <summary>An optional display name, used in logs and messages.</summary>
    public string? Name { get; init; }

    /// <summary>
    /// nuget.org's repository signing certificates (nuget.org rotates them), optionally restricted to packages owned by
    /// one of <paramref name="owners"/>.
    /// </summary>
    /// <param name="owners">nuget.org account names; none means any owner.</param>
    /// <returns>One repository signer per known nuget.org repository certificate.</returns>
    public static IEnumerable<AllowedSigner> NuGetOrg(params string[] owners)
    {
        ArgumentNullException.ThrowIfNull(owners);

        // SHA-256 fingerprints of "CN=NuGet.org Repository by Microsoft", from nuget.org's RepositorySignatures
        // resource (https://api.nuget.org/v3-index/repository-signatures/5.0.0/index.json, read 2026-10-17 by
        // spike/nuget-signature-verification). Check that resource when nuget.org announces a new certificate.
        (string Fingerprint, string Validity)[] certificates =
        [
            ("0E5F38F57DC1BCC806D8494F4F90FBCEDD988B46760709CBEEC6F4219AA6157D", "2018-04-10 to 2021-04-14"),
            ("5A2901D6ADA3D18260B9C6DFE2133C95D74B9EEF6AE0E5DC334C8454D1477DF4", "2021-02-16 to 2024-05-15"),
            ("1F4B311D9ACC115C8DC8018B5A49E00FCE6DA8E2855F9F014CA6F34570BC482D", "2024-02-23 to 2027-05-18"),
        ];

        return certificates.Select(c => new AllowedSigner
        {
            Kind = SignerKind.Repository,
            Fingerprint = c.Fingerprint,
            Owners = owners.ToList(),
            Name = $"nuget.org repository ({c.Validity})",
        }).ToList();
    }
}

namespace Alcove.Trust;

/// <summary>
/// The app's optional rules for what may be installed: which feeds, whether packages must be signed, and by whom.
/// Plugins always run with full trust; the trust policy only decides what gets installed.
/// </summary>
/// <remarks>
/// The policy applies to plugins <em>and</em> their dependencies. Signed packages are always integrity-checked, even
/// when signatures aren't required: a signed package changed since it was signed is always rejected.
/// Hash pinning in the state file is separate and always on.
/// </remarks>
public sealed class TrustPolicy
{
    /// <summary>
    /// Feed sources (URLs or folders) packages may be downloaded from, checked before download. Empty: any configured feed.
    /// </summary>
    public IList<string> AllowedFeeds { get; init => field = value ?? new List<string>(); } = new List<string>();

    /// <summary>
    /// Whether every package must carry a valid signature that chains to a trusted root, or matches an
    /// <see cref="AllowedSigner"/> with <see cref="AllowedSigner.AllowUntrustedRoot"/>. Defaults to <see langword="false"/>:
    /// unsigned packages are accepted.
    /// </summary>
    public bool RequireSignedPackages { get; set; }

    /// <summary>
    /// The signers accepted. Empty: any trusted signer. Otherwise every <em>signed</em> package must match at least one
    /// entry, by its author signature or its repository signature. Unsigned packages are governed only by
    /// <see cref="RequireSignedPackages"/>.
    /// </summary>
    public IList<AllowedSigner> AllowedSigners { get; init => field = value ?? new List<AllowedSigner>(); } = new List<AllowedSigner>();

    /// <summary>
    /// How certificate revocation is checked. Defaults to <see cref="RevocationCheck.Online"/>, as NuGet does.
    /// Choose <see cref="RevocationCheck.Offline"/> on air-gapped or unreliable networks.
    /// </summary>
    public RevocationCheck Revocation { get; set; } = RevocationCheck.Online;
}

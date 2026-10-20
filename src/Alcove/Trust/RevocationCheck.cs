namespace Alcove.Trust;

/// <summary>How certificate revocation is checked when verifying package signatures.</summary>
public enum RevocationCheck
{
    /// <summary>
    /// Online revocation checks (CRL/OCSP), as NuGet does. An unknown status is a warning, never a failure. A network
    /// that hangs (rather than refusing connections) can cost about 120 seconds per package.
    /// </summary>
    Online,

    /// <summary>Cached revocation data only; never touches the network.</summary>
    Offline,
}

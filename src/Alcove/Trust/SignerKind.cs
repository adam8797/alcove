namespace Alcove.Trust;

/// <summary>What an <see cref="AllowedSigner"/>'s certificate signs.</summary>
public enum SignerKind
{
    /// <summary>The package author's certificate (an author primary signature).</summary>
    Author,

    /// <summary>A repository's certificate (a repository primary signature or countersignature), e.g. nuget.org's.</summary>
    Repository,
}

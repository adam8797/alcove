using System.Reflection.Metadata;

namespace Alcove.Internal.Metadata;

/// <summary>
/// The identity WPF matches on: simple name, <c>AssemblyVersion</c> and public key token. Names compare
/// case-insensitively, as the runtime and WPF compare them.
/// </summary>
/// <param name="Name">Simple name.</param>
/// <param name="Version">The <c>AssemblyVersion</c> (not the NuGet version).</param>
/// <param name="PublicKeyToken">Lower-case hex token, or <see langword="null"/> when not strong-named.</param>
internal sealed record AssemblyIdentity(string Name, Version Version, string? PublicKeyToken)
{
    /// <summary>Whether name, version and token are all equal.</summary>
    public bool Equals(AssemblyIdentity? other) =>
        other is not null
        && string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase)
        && Version.Equals(other.Version)
        && string.Equals(PublicKeyToken, other.PublicKeyToken, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() => HashCode.Combine(
        StringComparer.OrdinalIgnoreCase.GetHashCode(Name),
        Version,
        PublicKeyToken is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(PublicKeyToken));

    public override string ToString() =>
        $"{Name}, Version={Version}, PublicKeyToken={PublicKeyToken ?? "null"}";

    internal static AssemblyIdentity FromDefinition(MetadataReader md)
    {
        var definition = md.GetAssemblyDefinition();
        var key = md.GetBlobBytes(definition.PublicKey);
        return new AssemblyIdentity(md.GetString(definition.Name), definition.Version, TokenFromKey(key));
    }

    internal static AssemblyIdentity FromReference(MetadataReader md, AssemblyReference reference)
    {
        var keyOrToken = md.GetBlobBytes(reference.PublicKeyOrToken);
        var token = (reference.Flags & System.Reflection.AssemblyFlags.PublicKey) != 0
            ? TokenFromKey(keyOrToken)
            : Hex(keyOrToken);
        return new AssemblyIdentity(md.GetString(reference.Name), reference.Version, token);
    }

    internal static AssemblyIdentity? FromNameInfo(AssemblyNameInfo info)
    {
        if (string.IsNullOrEmpty(info.Name))
        {
            return null;
        }

        var keyOrToken = info.PublicKeyOrToken.IsDefault ? [] : info.PublicKeyOrToken.AsSpan().ToArray();
        var token = (info.Flags & System.Reflection.AssemblyNameFlags.PublicKey) != 0 ? TokenFromKey(keyOrToken) : Hex(keyOrToken);
        return new AssemblyIdentity(info.Name, info.Version ?? new Version(0, 0, 0, 0), token);
    }

    private static string? TokenFromKey(byte[] publicKey)
    {
        if (publicKey.Length == 0)
        {
            return null;
        }

        // The token is the last 8 bytes of the key's SHA-1 hash, reversed (ECMA-335 II.6.3). Not used for security.
#pragma warning disable CA5350 // SHA-1 is what the format defines
        var hash = System.Security.Cryptography.SHA1.HashData(publicKey);
#pragma warning restore CA5350
        var token = hash.AsSpan(hash.Length - 8).ToArray();
        Array.Reverse(token);
        return Hex(token);
    }

    private static string? Hex(byte[] bytes) => bytes.Length == 0 ? null : Convert.ToHexStringLower(bytes);
}

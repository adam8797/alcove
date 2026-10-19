using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Alcove.IntegrationTests.Infrastructure;
using NuGet.Packaging;
using NuGet.Packaging.Signing;
using NuGet.Versioning;
using HashAlgorithmName = NuGet.Common.HashAlgorithmName;

namespace Alcove.IntegrationTests.Trust;

/// <summary>
/// Test packages for the trust policy, made at test time in a temporary folder: one unsigned package, copies of it
/// author-signed with two throw-away self-signed certificates (<see cref="AuthorFingerprint"/> and
/// <see cref="OtherFingerprint"/>), one repository-signed with owners, and a tampered copy of the author-signed one.
/// The certificates and their private keys only ever exist in memory: no file, no certificate store.
/// </summary>
public sealed class TrustTestPackages : IAsyncLifetime
{
    public const string PackageId = "Trust.Sample";

    /// <summary>The owners listed in <see cref="RepositorySigned"/>'s repository signature.</summary>
    public static readonly string[] RepositoryOwners = ["contoso", "fabrikam"];

    public static readonly NuGetVersion Version = NuGetVersion.Parse("1.0.0");

    private readonly TempDirectory _directory = new("alcove-trust");

    public string Unsigned { get; private set; } = "";

    /// <summary>Author-signed with the "allowed" certificate.</summary>
    public string AuthorSigned { get; private set; } = "";

    /// <summary>Author-signed with another certificate.</summary>
    public string OtherSigned { get; private set; } = "";

    /// <summary>Repository-signed (owners <see cref="RepositoryOwners"/>) with a third certificate.</summary>
    public string RepositorySigned { get; private set; } = "";

    /// <summary><see cref="AuthorSigned"/> with one byte of its payload flipped.</summary>
    public string Tampered { get; private set; } = "";

    /// <summary>A file that isn't a zip archive.</summary>
    public string Garbage { get; private set; } = "";

    public string AuthorFingerprint { get; private set; } = "";

    public string OtherFingerprint { get; private set; } = "";

    public string RepositoryFingerprint { get; private set; } = "";

    /// <summary>A path in the temporary folder.</summary>
    public string Combine(string name) => _directory.Combine(name);

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        Unsigned = CreatePackage(_directory.Combine("unsigned.nupkg"));

        using var author = CreateCodeSigningCertificate("Alcove Test Author");
        using var other = CreateCodeSigningCertificate("Alcove Other Author");
        using var repository = CreateCodeSigningCertificate("Alcove Test Repository");
        AuthorFingerprint = author.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256);
        OtherFingerprint = other.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256);
        RepositoryFingerprint = repository.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256);

        using (var request = new AuthorSignPackageRequest(author, HashAlgorithmName.SHA256))
        {
            AuthorSigned = await SignAsync(request, "author-signed.nupkg", ct);
        }

        using (var request = new AuthorSignPackageRequest(other, HashAlgorithmName.SHA256))
        {
            OtherSigned = await SignAsync(request, "other-signed.nupkg", ct);
        }

        using (var request = new RepositorySignPackageRequest(repository, HashAlgorithmName.SHA256, HashAlgorithmName.SHA256,
                   new Uri("https://feed.example.com/v3/index.json"), RepositoryOwners))
        {
            RepositorySigned = await SignAsync(request, "repository-signed.nupkg", ct);
        }

        Tampered = _directory.Combine("tampered.nupkg");
        File.Copy(AuthorSigned, Tampered);
        FlipByteInEntry(Tampered, "content/payload.bin");

        Garbage = _directory.Combine("garbage.nupkg");
        File.WriteAllText(Garbage, "not a package");
    }

    public ValueTask DisposeAsync()
    {
        _directory.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>A minimal package with a random payload.</summary>
    public static string CreatePackage(string path, string id = PackageId, string version = "1.0.0")
    {
        var builder = new PackageBuilder { Id = id, Version = NuGetVersion.Parse(version), Description = "Trust policy test package." };
        builder.Authors.Add("Alcove tests");
        builder.Files.Add(new PhysicalPackageFile(new MemoryStream(RandomNumberGenerator.GetBytes(4096))) { TargetPath = "content/payload.bin" });
        using (var stream = File.Create(path))
        {
            builder.Save(stream);
        }

        return path;
    }

    private async Task<string> SignAsync(SignPackageRequest request, string fileName, CancellationToken cancellationToken)
    {
        var output = _directory.Combine(fileName);
        using var options = SigningOptions.CreateFromFilePaths(
            Unsigned, output, overwrite: true, new X509SignatureProvider(timestampProvider: null), NuGet.Common.NullLogger.Instance);
        await SigningUtility.SignAsync(options, request, cancellationToken);
        return output;
    }

    /// <summary>A self-signed code-signing certificate, as <c>openssl req -x509 ... -addext extendedKeyUsage=codeSigning</c> makes.</summary>
    internal static X509Certificate2 CreateCodeSigningCertificate(string commonName)
    {
        using var key = RSA.Create(3072);
        var request = new CertificateRequest($"CN={commonName}, O=Alcove Tests", key,
            System.Security.Cryptography.HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.3")], false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        var now = DateTimeOffset.UtcNow;
        return request.CreateSelfSigned(now.AddDays(-1), now.AddDays(30));
    }

    /// <summary>Flips one byte in the middle of a zip entry's stored data, leaving every zip header intact.</summary>
    internal static void FlipByteInEntry(string path, string entryName)
    {
        var bytes = File.ReadAllBytes(path);
        var endOfCentralDirectory = -1;
        for (var i = bytes.Length - 22; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i)) == 0x06054b50)
            {
                endOfCentralDirectory = i;
                break;
            }
        }

        Assert.True(endOfCentralDirectory >= 0, "No end of central directory record.");
        var entryCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(endOfCentralDirectory + 10));
        var position = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(endOfCentralDirectory + 16));
        for (var entry = 0; entry < entryCount; entry++)
        {
            var compressedSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position + 20));
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(position + 28));
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(position + 30));
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(position + 32));
            var localHeader = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position + 42));
            if (Encoding.UTF8.GetString(bytes, position + 46, nameLength) == entryName)
            {
                var localNameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(localHeader + 26));
                var localExtraLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(localHeader + 28));
                bytes[localHeader + 30 + localNameLength + localExtraLength + (compressedSize / 2)] ^= 0xFF;
                File.WriteAllBytes(path, bytes);
                return;
            }

            position += 46 + nameLength + extraLength + commentLength;
        }

        Assert.Fail($"'{entryName}' isn't in '{path}'.");
    }
}

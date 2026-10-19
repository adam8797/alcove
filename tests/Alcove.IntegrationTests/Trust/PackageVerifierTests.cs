using Alcove.Internal.Trust;
using Alcove.IntegrationTests.Infrastructure;

namespace Alcove.IntegrationTests.Trust;

/// <summary>
/// Every trust policy branch against real packages signed at test time with self-signed certificates (no network).
/// The expectations match spike/nuget-signature-verification's matrix.
/// </summary>
public sealed class PackageVerifierTests(TrustTestPackages packages) : IClassFixture<TrustTestPackages>
{
    private readonly ListLoggerFactory _logs = new();

    // --- No signature requirements (the default policy) ---------------------------------------------------------

    [Fact]
    public async Task DefaultPolicy_AcceptsUnsigned_WithAnInfo()
    {
        var decision = await VerifyAsync(new TrustPolicy(), packages.Unsigned);

        Assert.True(decision.Allowed);
        Assert.False(decision.IsSigned);
        var issue = Assert.Single(decision.Issues);
        Assert.Equal((TrustIssueSeverity.Info, TrustIssueCodes.UnsignedAccepted), (issue.Severity, issue.Code));
    }

    [Fact]
    public async Task DefaultPolicy_AcceptsAnUntrustedSignature_WithWarningsOnly()
    {
        var decision = await VerifyAsync(new TrustPolicy(), packages.OtherSigned);

        Assert.True(decision.Allowed);
        Assert.True(decision.IsSigned);
        Assert.DoesNotContain(decision.Issues, i => i.Severity == TrustIssueSeverity.Error);
    }

    [Fact]
    public async Task DefaultPolicy_RejectsATamperedSignedPackage()
    {
        var decision = await VerifyAsync(new TrustPolicy(), packages.Tampered);

        AssertRejected(decision, TrustIssueCodes.Tampered);
        Assert.Contains("NU3008", decision.Issues.Single(i => i.Code == TrustIssueCodes.Tampered).Message, StringComparison.Ordinal);
        Assert.Single(_logs.WithEventId(4005));
    }

    // --- RequireSignedPackages, any trusted signer -----------------------------------------------------------

    [Fact]
    public async Task RequireSigned_RejectsUnsigned()
    {
        var decision = await VerifyAsync(new TrustPolicy { RequireSignedPackages = true }, packages.Unsigned);

        AssertRejected(decision, TrustIssueCodes.NotSigned);
        Assert.False(decision.IsSigned);
        Assert.Contains("NU3004", decision.Issues[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequireSigned_RejectsASelfSignedAuthor_NotInAllowedSigners()
    {
        var decision = await VerifyAsync(new TrustPolicy { RequireSignedPackages = true }, packages.AuthorSigned);

        AssertRejected(decision, TrustIssueCodes.UntrustedCertificate);
        Assert.Contains("AllowUntrustedRoot", decision.Issues.Single(i => i.Code == TrustIssueCodes.UntrustedCertificate).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tampered_AlwaysFails(bool requireSigned)
    {
        var policy = new TrustPolicy
        {
            RequireSignedPackages = requireSigned,
            AllowedSigners = { Author(packages.AuthorFingerprint, allowUntrustedRoot: true) },
        };

        AssertRejected(await VerifyAsync(policy, packages.Tampered), TrustIssueCodes.Tampered);
    }

    // --- AllowedSigners: author by fingerprint ---------------------------------------------------------------

    [Theory]
    [InlineData(RevocationCheck.Online)]
    [InlineData(RevocationCheck.Offline)]
    public async Task AllowedAuthor_WithAllowUntrustedRoot_IsAccepted(RevocationCheck revocation)
    {
        var policy = RequireAuthor(allowUntrustedRoot: true);
        policy.Revocation = revocation;

        var decision = await VerifyAsync(policy, packages.AuthorSigned);

        Assert.True(decision.Allowed, Describe(decision));
        Assert.True(decision.IsSigned);
        Assert.DoesNotContain(decision.Issues, i => i.Severity == TrustIssueSeverity.Error);
        Assert.Contains(decision.Issues, i => i.Code == TrustIssueCodes.NotTimestamped);   // test packages have no timestamp
        Assert.Single(_logs.WithEventId(4004));
    }

    [Fact]
    public async Task AllowedAuthor_WithoutAllowUntrustedRoot_IsRejected()
    {
        var decision = await VerifyAsync(RequireAuthor(allowUntrustedRoot: false), packages.AuthorSigned);

        AssertRejected(decision, TrustIssueCodes.UntrustedCertificate);
    }

    [Fact]
    public async Task AllowedAuthor_AnotherAuthor_IsRejected()
    {
        var decision = await VerifyAsync(RequireAuthor(allowUntrustedRoot: true), packages.OtherSigned);

        AssertRejected(decision, TrustIssueCodes.SignerNotAllowed);
        Assert.Contains("NU3034", decision.Issues.Single(i => i.Code == TrustIssueCodes.SignerNotAllowed).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AllowedAuthor_Unsigned_IsRejectedWhenSignaturesAreRequired()
    {
        AssertRejected(await VerifyAsync(RequireAuthor(allowUntrustedRoot: true), packages.Unsigned), TrustIssueCodes.NotSigned);
    }

    [Fact]
    public async Task AllowedAuthor_FingerprintIsMatchedInAnyCaseWithSeparators()
    {
        var fingerprint = string.Join(':', packages.AuthorFingerprint.ToLowerInvariant().Chunk(2).Select(c => new string(c)));
        var policy = new TrustPolicy { RequireSignedPackages = true, AllowedSigners = { Author(fingerprint, allowUntrustedRoot: true) } };

        Assert.True((await VerifyAsync(policy, packages.AuthorSigned)).Allowed);
    }

    [Fact]
    public async Task AllowedSignersWithoutRequiredSignatures_AcceptUnsigned_ButRejectOtherSigners()
    {
        var policy = new TrustPolicy { AllowedSigners = { Author(packages.AuthorFingerprint, allowUntrustedRoot: true) } };

        Assert.True((await VerifyAsync(policy, packages.Unsigned)).Allowed);
        Assert.True((await VerifyAsync(policy, packages.AuthorSigned)).Allowed);
        AssertRejected(await VerifyAsync(policy, packages.OtherSigned), TrustIssueCodes.SignerNotAllowed);
    }

    // --- AllowedSigners: repository certificate and owners ---------------------------------------------------

    [Theory]
    [InlineData(new string[0], true)]
    [InlineData(new[] { "fabrikam" }, true)]
    [InlineData(new[] { "someone-else", "contoso" }, true)]
    [InlineData(new[] { "someone-else" }, false)]
    public async Task AllowedRepository_MatchesOwners(string[] owners, bool allowed)
    {
        var policy = new TrustPolicy
        {
            RequireSignedPackages = true,
            AllowedSigners =
            {
                new AllowedSigner { Kind = SignerKind.Repository, Fingerprint = packages.RepositoryFingerprint, Owners = owners, AllowUntrustedRoot = true },
            },
        };

        var decision = await VerifyAsync(policy, packages.RepositorySigned);

        if (allowed)
        {
            Assert.True(decision.Allowed, Describe(decision));
        }
        else
        {
            AssertRejected(decision, TrustIssueCodes.SignerNotAllowed);
        }
    }

    [Fact]
    public async Task AllowedRepository_DoesNotMatchAnAuthorSignatureWithTheSameFingerprint()
    {
        var policy = new TrustPolicy
        {
            RequireSignedPackages = true,
            AllowedSigners = { new AllowedSigner { Kind = SignerKind.Repository, Fingerprint = packages.AuthorFingerprint, AllowUntrustedRoot = true } },
        };

        AssertRejected(await VerifyAsync(policy, packages.AuthorSigned), TrustIssueCodes.SignerNotAllowed);
    }

    // --- Other failures --------------------------------------------------------------------------------------

    [Fact]
    public async Task InvalidPolicy_RejectsEverything()
    {
        var policy = new TrustPolicy { AllowedSigners = { Author("not-a-fingerprint", allowUntrustedRoot: true) } };

        var verifier = CreateVerifier(policy);
        AssertRejected(await verifier.VerifyPackageAsync(packages.Unsigned, TrustTestPackages.PackageId, TrustTestPackages.Version, TestContext.Current.CancellationToken), TrustIssueCodes.InvalidPolicy);
        AssertRejected(verifier.CheckFeed("https://nuget.example.com/v3/index.json"), TrustIssueCodes.InvalidPolicy);
        Assert.NotEmpty(_logs.WithEventId(4002));
    }

    [Fact]
    public async Task AnotherPackageThanRequested_IsRejected()
    {
        var decision = await CreateVerifier(new TrustPolicy())
            .VerifyPackageAsync(packages.Unsigned, "Trust.Other", TrustTestPackages.Version, TestContext.Current.CancellationToken);

        AssertRejected(decision, TrustIssueCodes.IdentityMismatch);
    }

    [Fact]
    public async Task VersionIsComparedSemantically()
    {
        var decision = await CreateVerifier(new TrustPolicy())
            .VerifyPackageAsync(packages.Unsigned, "trust.sample", NuGet.Versioning.NuGetVersion.Parse("1.0"), TestContext.Current.CancellationToken);

        Assert.True(decision.Allowed, Describe(decision));
    }

    [Fact]
    public async Task NotAPackage_IsRejectedAsUnreadable()
    {
        AssertRejected(await VerifyAsync(new TrustPolicy(), packages.Garbage), TrustIssueCodes.Unreadable);
    }

    [Fact]
    public async Task PolicyIsCopiedWhenTheVerifierIsCreated()
    {
        var policy = new TrustPolicy();
        var verifier = CreateVerifier(policy);
        policy.RequireSignedPackages = true;

        var decision = await verifier.VerifyPackageAsync(packages.Unsigned, TrustTestPackages.PackageId, TrustTestPackages.Version, TestContext.Current.CancellationToken);

        Assert.True(decision.Allowed);
    }

    private TrustPolicy RequireAuthor(bool allowUntrustedRoot) => new()
    {
        RequireSignedPackages = true,
        AllowedSigners = { Author(packages.AuthorFingerprint, allowUntrustedRoot) },
    };

    private static AllowedSigner Author(string fingerprint, bool allowUntrustedRoot) =>
        new() { Kind = SignerKind.Author, Fingerprint = fingerprint, AllowUntrustedRoot = allowUntrustedRoot, Name = "Alcove Test Author" };

    private PackageVerifier CreateVerifier(TrustPolicy policy) => new(policy, _logs.CreateLogger("Trust"));

    private Task<TrustDecision> VerifyAsync(TrustPolicy policy, string nupkg) =>
        CreateVerifier(policy).VerifyPackageAsync(nupkg, TrustTestPackages.PackageId, TrustTestPackages.Version, TestContext.Current.CancellationToken);

    internal static void AssertRejected(TrustDecision decision, string expectedCode)
    {
        Assert.False(decision.Allowed, Describe(decision));
        Assert.Contains(decision.Issues, i => i.Severity == TrustIssueSeverity.Error && i.Code == expectedCode);
    }

    internal static string Describe(TrustDecision decision) =>
        $"Allowed={decision.Allowed} IsSigned={decision.IsSigned}: {string.Join(" | ", decision.Issues)}";
}

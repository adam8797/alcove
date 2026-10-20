using Alcove.IntegrationTests.Infrastructure;
using Alcove.Trust;
using NuGet.Versioning;

namespace Alcove.IntegrationTests.Trust;

/// <summary>
/// nuget.org's repository (and author) signatures against the shipped root bundle. These download real packages,
/// so they need the network: they only run with <c>ALCOVE_NETWORK_TESTS=1</c>, e.g.
/// <c>ALCOVE_NETWORK_TESTS=1 dotnet test --filter Category=Network</c>.
/// </summary>
[Trait("Category", "Network")]
public sealed class NuGetOrgSignatureTests : IDisposable
{
    private readonly TempDirectory _downloads = new("alcove-nugetorg");

    public void Dispose() => _downloads.Dispose();

    [Theory]
    [InlineData("Newtonsoft.Json", "13.0.3")]   // author (Json.NET) + nuget.org 2021 repository countersignature
    [InlineData("Serilog", "4.0.0")]            // nuget.org 2024 repository signature only
    [InlineData("Dapper", "2.1.35")]            // nuget.org 2021 repository signature only
    public async Task RequireSigned_AnyTrustedSigner_AcceptsNuGetOrgPackages(string id, string version)
    {
        var decision = await VerifyAsync(new TrustPolicy { RequireSignedPackages = true }, id, version);

        Assert.True(decision.Allowed, PackageVerifierTests.Describe(decision));
        Assert.True(decision.IsSigned);
    }

    [Theory]
    [InlineData("Newtonsoft.Json", "13.0.3", true)]   // owners include jamesnk
    [InlineData("Serilog", "4.0.0", true)]            // owner serilog
    [InlineData("Dapper", "2.1.35", false)]
    public async Task NuGetOrgWithOwners_AcceptsOnlyThoseOwners(string id, string version, bool allowed)
    {
        var policy = new TrustPolicy { RequireSignedPackages = true, AllowedSigners = [.. AllowedSigner.NuGetOrg("jamesnk", "serilog")] };

        var decision = await VerifyAsync(policy, id, version);

        if (allowed)
        {
            Assert.True(decision.Allowed, PackageVerifierTests.Describe(decision));
        }
        else
        {
            PackageVerifierTests.AssertRejected(decision, TrustIssueCodes.SignerNotAllowed);
        }
    }

    [Fact]
    public async Task NuGetOrg_AnyOwner_AcceptsEveryRepositoryCertificateInUse()
    {
        var policy = new TrustPolicy { RequireSignedPackages = true, AllowedSigners = [.. AllowedSigner.NuGetOrg()] };

        foreach (var (id, version) in new[] { ("Newtonsoft.Json", "13.0.3"), ("Serilog", "4.0.0"), ("Dapper", "2.1.35") })
        {
            var decision = await VerifyAsync(policy, id, version);
            Assert.True(decision.Allowed, $"{id}: {PackageVerifierTests.Describe(decision)}");
        }
    }

    private async Task<TrustDecision> VerifyAsync(TrustPolicy policy, string id, string version)
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("ALCOVE_NETWORK_TESTS") == "1",
            "Needs the network (nuget.org); set ALCOVE_NETWORK_TESTS=1 to run.");
        var ct = TestContext.Current.CancellationToken;
        var path = _downloads.Combine($"{id.ToLowerInvariant()}.{version}.nupkg");
        if (!File.Exists(path))
        {
            using var http = new HttpClient();
            var bytes = await http.GetByteArrayAsync(
                $"https://api.nuget.org/v3-flatcontainer/{id.ToLowerInvariant()}/{version}/{id.ToLowerInvariant()}.{version}.nupkg", ct);
            await File.WriteAllBytesAsync(path, bytes, ct);
        }

        var verifier = new PackageVerifier(policy, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        return await verifier.VerifyPackageAsync(path, id, NuGetVersion.Parse(version), ct);
    }
}

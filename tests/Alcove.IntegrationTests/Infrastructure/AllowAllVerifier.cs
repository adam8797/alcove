using Alcove.Internal.Trust;
using NuGet.Versioning;

namespace Alcove.IntegrationTests.Infrastructure;

/// <summary>A test double that allows every feed and package (tests of resolution, not of the trust policy).</summary>
internal sealed class AllowAllVerifier : IPackageVerifier
{
    public static AllowAllVerifier Instance { get; } = new();

    public bool RemembersAcceptances => true;

    public TrustDecision CheckFeed(string source) => new(true, false, []);

    public TrustDecision CheckKeptSource(string packageId, NuGetVersion version, string? source) => new(true, false, []);

    public Task<TrustDecision> VerifyPackageAsync(string nupkgPath, string packageId, NuGetVersion version, CancellationToken cancellationToken) =>
        Task.FromResult(new TrustDecision(true, false, []));
}

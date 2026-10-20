using System.Collections.Concurrent;
using Alcove.Storage;
using NuGet.Versioning;

namespace Alcove.Trust;

/// <summary>
/// The trust policy's verdicts on packages, remembered for a host's lifetime (in memory) per policy
/// (<see cref="IPackageVerifier.PolicyFingerprint"/>), package ID and version. A plan that uses a package already in
/// the plugin root (kept, orphaned, or shared with other plugins) has its recorded source feed checked, then has it
/// verified again from its kept <c>.nupkg</c>, unless the same policy accepted it recently in this host. Only
/// acceptances are remembered: for the host's lifetime when the verifier allows it
/// (<see cref="IPackageVerifier.RemembersAcceptances"/>: offline revocation checks), otherwise for
/// <see cref="OnlineAcceptanceLifetime"/>, so an online check sees a revocation soon. A rejection may be transient (e.g.
/// an online revocation check that failed), so it isn't remembered.
/// </summary>
internal sealed class TrustVerdicts(TimeProvider? clock = null)
{
    /// <summary>How long an acceptance is remembered when the verifier checks revocation online.</summary>
    internal static readonly TimeSpan OnlineAcceptanceLifetime = TimeSpan.FromMinutes(10);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<(string Policy, string Id, NuGetVersion Version), DateTimeOffset> _accepted = new();

    /// <summary>Verifies a downloaded package (always, even if the same package was accepted before) and remembers an acceptance.</summary>
    public async Task<TrustDecision> VerifyDownloadAsync(IPackageVerifier verifier, string nupkgPath, string id, NuGetVersion version, CancellationToken cancellationToken)
    {
        var decision = await verifier.VerifyPackageAsync(nupkgPath, id, version, cancellationToken).ConfigureAwait(false);
        Remember(verifier, id, version, decision);
        return decision;
    }

    /// <summary>
    /// Checks a package already in the plugin root: its recorded source feed (always), then its kept <c>.nupkg</c>,
    /// unless the policy accepted it before and that acceptance is still remembered (then <see langword="null"/>). A missing <c>.nupkg</c> is a rejection: the
    /// policy can't be applied.
    /// </summary>
    /// <param name="verifier">Applies the policy.</param>
    /// <param name="layout">The plugin root's layout.</param>
    /// <param name="id">The package ID.</param>
    /// <param name="version">The package version.</param>
    /// <param name="source">The feed the package was downloaded from, as recorded; <see langword="null"/> if unknown.</param>
    /// <param name="cancellationToken">Cancels the verification.</param>
    public async Task<TrustDecision?> VerifyKeptAsync(IPackageVerifier verifier, PackageLayout layout, string id, NuGetVersion version, string? source, CancellationToken cancellationToken)
    {
        var feed = verifier.CheckKeptSource(id, version, source);
        if (!feed.Allowed)
        {
            return feed;
        }

        if (_accepted.TryGetValue(Key(verifier, id, version), out var accepted)
            && (verifier.RemembersAcceptances || _clock.GetUtcNow() - accepted < OnlineAcceptanceLifetime))
        {
            return null;
        }

        var nupkg = layout.GetPackageFilePath(id, version);
        if (!File.Exists(nupkg))
        {
            return new TrustDecision(false, false,
                [new TrustIssue(TrustIssueSeverity.Error, TrustIssueCodes.Unreadable,
                    $"{id} {version} is in the plugin root without its package file ({Path.GetFileName(nupkg)}), so the trust policy can't be applied to it. Restore it (RestoreAsync) or reinstall it.")]);
        }

        var decision = await verifier.VerifyPackageAsync(nupkg, id, version, cancellationToken).ConfigureAwait(false);
        Remember(verifier, id, version, decision);
        return decision;
    }

    private void Remember(IPackageVerifier verifier, string id, NuGetVersion version, TrustDecision decision)
    {
        if (decision.Allowed)
        {
            _accepted[Key(verifier, id, version)] = _clock.GetUtcNow();
        }
    }

    private static (string, string, NuGetVersion) Key(IPackageVerifier verifier, string id, NuGetVersion version) =>
        (verifier.PolicyFingerprint, id.ToLowerInvariant(), version);
}

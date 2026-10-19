using Alcove.Internal.Feeds;
using Alcove.Internal.Hosting;
using Alcove.Internal.Install;
using Alcove.Internal.IO;
using Alcove.Internal.State;
using Alcove.Internal.Trust;
using Microsoft.Extensions.Logging;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Versioning;

namespace Alcove.Internal.Lifecycle;

/// <summary>
/// <c>RestoreAsync</c> (architecture §6): under the root lock, downloads every package the state file pins that's
/// missing from <c>packages/</c>, applies the trust policy, checks its <c>contentHash</c> against the pinned one,
/// extracts it and records its files again. It resolves nothing and changes no version.
/// </summary>
internal sealed class PackageRestorer(HostInventory host, PackageLayout layout, FeedConfiguration feeds, IPackageVerifier verifier, ILogger logger)
{
    public async Task<RestoreResult> RestoreAsync(TimeSpan lockTimeout, IProgress<InstallProgress>? progress, CancellationToken cancellationToken)
    {
        using var session = new FeedSession(feeds, verifier, logger, cancellationToken);
        var issues = session.FeedIssues
            .Where(f => f.Issue.Severity != TrustIssueSeverity.Info)
            .Select(f => ToIssue(f.Issue, null))
            .ToList();
        using var rootLock = await RootLock.AcquireAsync(layout.Root, lockTimeout, cancellationToken).ConfigureAwait(false);
        var store = new StateStore(layout.Root);
        StateDocument state;
        try
        {
            state = store.Read();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return new RestoreResult([], [.. issues, new ValidationIssue(ValidationSeverity.Error, ValidationCodes.StateFileUnreadable, ex.Message)]);
        }

        var paths = new VersionFolderPathResolver(layout.PackagesDirectory, isLowercase: true);
        var recorder = new PackageRecorder(host, layout);
        var results = new List<RestoredPackage>();
        var files = new Dictionary<(string, NuGetVersion), IReadOnlyList<FileRecord>>();
        foreach (var pinned in PinnedPackage.All(state).Where(p => !File.Exists(paths.GetNupkgMetadataPath(p.Id, p.Version))))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var failure = await RestoreOneAsync(pinned, session, recorder, issues, progress, cancellationToken).ConfigureAwait(false);
            if (failure is null)
            {
                files[(pinned.Id.ToLowerInvariant(), pinned.Version)] = recorder.Record(pinned.Id, pinned.Version).Files;
                LifecycleLog.Restored(logger, pinned.Id, pinned.Version.ToNormalizedString());
            }
            else
            {
                LifecycleLog.RestoreFailed(logger, pinned.Id, pinned.Version.ToNormalizedString(), failure);
            }

            results.Add(new RestoredPackage(pinned.Id, pinned.Version, failure is null, failure));
        }

        if (files.Count > 0)
        {
            // Extraction writes new files (.nupkg.metadata and the like): record them, so startup validation accepts them.
            progress?.Report(new InstallProgress(InstallStep.Recording, files.Keys.First().Item1));
            store.Write(rootLock, PinnedPackage.WithFiles(state, files));
        }

        return new RestoreResult(results, issues);
    }

    /// <summary>Restores one package; returns why it failed, or <see langword="null"/>.</summary>
    private async Task<string?> RestoreOneAsync(PinnedPackage pinned, FeedSession session, PackageRecorder recorder, List<ValidationIssue> issues, IProgress<InstallProgress>? progress, CancellationToken cancellationToken)
    {
        if (pinned.ContentHash is null)
        {
            return "The state file pins no contentHash for it, so a download can't be checked.";
        }

        var identity = new PackageIdentity(pinned.Id, pinned.Version);
        progress?.Report(new InstallProgress(InstallStep.Downloading, pinned.Id, pinned.Version));
        FeedPackage? package = null;
        var errors = new List<string>();
        foreach (var repository in session.Repositories)
        {
            try
            {
                package = await session.FetchAsync(repository, identity).ConfigureAwait(false);
            }
            catch (FeedException ex)
            {
                errors.Add(ex.Message);
            }

            if (package is not null)
            {
                break;
            }
        }

        if (package is null)
        {
            return errors.Count > 0
                ? string.Join(" ", errors)
                : $"It isn't on {(session.Repositories.Count == 0 ? "any feed (none is configured or allowed)" : "the feeds")}.";
        }

        progress?.Report(new InstallProgress(InstallStep.VerifyingSignatures, pinned.Id, pinned.Version));
        var decision = await verifier.VerifyPackageAsync(package.NupkgPath, pinned.Id, pinned.Version, cancellationToken).ConfigureAwait(false);
        issues.AddRange(decision.Issues.Where(i => i.Severity != TrustIssueSeverity.Info).Select(i => ToIssue(i, pinned.Id)));
        if (!decision.Allowed)
        {
            var why = decision.Issues.Where(i => i.Severity == TrustIssueSeverity.Error).Select(i => $"[{i.Code}] {i.Message}");
            return $"The trust policy rejected it: {string.Join(" ", why)}";
        }

        var hash = PackageRecorder.ComputeContentHash(package.NupkgPath);
        if (!string.Equals(hash, pinned.ContentHash, StringComparison.Ordinal))
        {
            return $"The package on '{package.Source.PackageSource.Source}' has the contentHash {hash}, but the state file pins {pinned.ContentHash}: it isn't the package that was installed.";
        }

        progress?.Report(new InstallProgress(InstallStep.Extracting, pinned.Id, pinned.Version));
        await recorder.ExtractAsync(package.NupkgPath, identity, package.Source.PackageSource.Source, session.NuGetLogger, cancellationToken).ConfigureAwait(false);
        return null;
    }

    private static ValidationIssue ToIssue(TrustIssue issue, string? packageId) =>
        new(issue.Severity == TrustIssueSeverity.Error ? ValidationSeverity.Error : ValidationSeverity.Warning, issue.Code, issue.Message, packageId);
}

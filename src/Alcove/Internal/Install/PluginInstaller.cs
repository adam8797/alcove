using Alcove.Internal.Feeds;
using Alcove.Internal.Hosting;
using Alcove.Internal.IO;
using Alcove.Internal.Lifecycle;
using Alcove.Internal.Loading;
using Alcove.Internal.State;
using Alcove.Internal.Trust;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NuGet.Versioning;

namespace Alcove.Internal.Install;

/// <summary>What <see cref="PluginInstaller"/> did, before the hot-load step.</summary>
internal sealed class InstallOutcome
{
    /// <summary>Set when nothing was written: <see cref="InstallStatus.Failed"/> or <see cref="InstallStatus.Incompatible"/>.</summary>
    public InstallStatus? Failure { get; init; }

    public IReadOnlyList<string> Reasons { get; init; } = [];

    public NuGetVersion? Version { get; init; }

    /// <summary>The plugin is already installed (at a version the request accepts); nothing changed.</summary>
    public PluginState? AlreadyInstalled { get; init; }

    /// <summary>The new plugins, dependencies first.</summary>
    public IReadOnlyList<string> NewPlugins { get; init; } = [];

    /// <summary>The cells resolved again.</summary>
    public IReadOnlyList<string> Cells { get; init; } = [];

    /// <summary>Validation of the state as written: the cells' plans and the load order.</summary>
    public LoadPlan? Plan { get; init; }

    /// <summary>New validation issues and trust issues.</summary>
    public IReadOnlyList<ValidationIssue> Issues { get; init; } = [];

    /// <summary>
    /// The requested plugin was staged for uninstall: the install cancelled that. With <see cref="AlreadyInstalled"/> it
    /// stays at its version; otherwise it switches to <see cref="Version"/> at the next startup.
    /// </summary>
    public string? UninstallCancelled { get; init; }

    /// <summary>Staged switches (e.g. "A 1.0.0 -> 2.0.0") of plugins in the resolved cells: the install was resolved with them.</summary>
    public IReadOnlyList<string> StagedInCells { get; init; } = [];

    public static InstallOutcome Fail(InstallStatus status, IEnumerable<string> reasons, NuGetVersion? version = null, IReadOnlyList<ValidationIssue>? issues = null) =>
        new() { Failure = status, Reasons = reasons.ToList(), Version = version, Issues = issues ?? [] };
}

/// <summary>
/// The install pipeline up to the hot-load step (architecture §6): under the root lock, re-reads the state, resolves
/// the target cells jointly (<see cref="InstallPlanner"/>), checks the feeds and then each downloaded package against
/// the trust policy, extracts, records (hashes, files, assemblies, WPF relevance, exposed dependencies), validates
/// the would-be state and writes it.
/// </summary>
internal sealed class PluginInstaller(
    HostInventory host,
    PackageLayout layout,
    PluginHostOptions options,
    FeedConfiguration feeds,
    IPackageVerifier verifier,
    TrustVerdicts verdicts,
    ILogger logger)
{
    private static readonly string[] s_incompatibleCodes =
        [ValidationCodes.HostOwnedConflict, ValidationCodes.HostOwnedMissing, ValidationCodes.ManifestSchemaUnsupported];

    public async Task<InstallOutcome> InstallAsync(string packageId, VersionRange? range, bool includePrerelease, TimeSpan lockTimeout, IProgress<InstallProgress>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new InstallProgress(InstallStep.Resolving, packageId));
        using var session = new FeedSession(feeds, verifier, logger, cancellationToken);
        var issues = session.FeedIssues.Select(f => ToIssue(f.Issue, null)).OfType<ValidationIssue>().ToList();
        using var rootLock = await RootLock.AcquireAsync(layout.Root, lockTimeout, cancellationToken).ConfigureAwait(false);

        StateDocument state;
        try
        {
            state = new StateStore(layout.Root).Read();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return InstallOutcome.Fail(InstallStatus.Failed, [$"The state file can't be read: {ex.Message}"], issues: issues);
        }

        // Plan against the state the next startup loads, so staged switches in the affected cells are resolved with
        // the install (and their staged targets resolved again) instead of going stale.
        var effective = StagedChanges.Effective(state);
        if (effective.Find(packageId) is { } existing)
        {
            return range is null || range.Satisfies(existing.Version)
                ? new InstallOutcome { AlreadyInstalled = existing, Version = existing.Version, Issues = issues }
                : InstallOutcome.Fail(InstallStatus.Failed,
                    [$"{existing.Id} {existing.Version} is already installed. InstallAsync doesn't update an installed plugin."], existing.Version, issues);
        }

        var uninstalling = state.Find(packageId);
        if (uninstalling is not null && (range is null || range.Satisfies(uninstalling.Version)))
        {
            return CancelUninstall(rootLock, state, effective, uninstalling, issues);
        }

        InstallPlan plan;
        try
        {
            plan = await new InstallPlanner(host, layout, options, session, logger)
                .PlanAsync(effective, packageId, range, includePrerelease, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (FeedException.Find(ex) is { } feedError)
        {
            // Also when the walker wrapped it.
            return InstallOutcome.Fail(InstallStatus.Failed, [feedError.Message], issues: issues);
        }

        if (plan.Failure is { } failure)
        {
            return InstallOutcome.Fail(failure, plan.Reasons, plan.Version, issues);
        }

        var recorder = new PackageRecorder(host, layout);
        if (await VerifyAndExtractAsync(plan, state, session, recorder, issues, progress, cancellationToken).ConfigureAwait(false) is { } rejected)
        {
            return InstallOutcome.Fail(InstallStatus.Failed, [rejected], plan.Version, issues);
        }

        progress?.Report(new InstallProgress(InstallStep.Recording, packageId, plan.Version));
        var states = plan.Plugins.Select(p => Record(recorder, p)).ToList();
        var wouldBe = PackageRecorder.Merge(effective, states);

        // Validate the would-be state; anything new that blocks a plugin fails the install (nothing is written).
        var validator = new PluginValidator(host, layout, options.Cells, NullLoggerFactory.Instance);
        var before = validator.Validate(effective, cancellationToken).Report;
        var after = validator.Validate(wouldBe, cancellationToken);
        var added = HotLoadRule.Added(before, after.Report);
        issues.AddRange(added);
        var errors = added.Where(i => i.Severity == ValidationSeverity.Error).ToList();
        if (errors.Count > 0)
        {
            var status = errors.Any(e => s_incompatibleCodes.Contains(e.Code)) ? InstallStatus.Incompatible : InstallStatus.Failed;
            return InstallOutcome.Fail(status, errors.Select(e => e.Message), plan.Version, issues);
        }

        var request = new StageRequest(packageId, StagedChangeKind.Update, OthersReason: $"Resolved again for the install of {packageId} {plan.Version}.");
        new StateStore(layout.Root).Write(rootLock, StagedChanges.StageInstall(state, wouldBe, request, TimeProvider.System.GetUtcNow()));
        var stagedInCells = after.Plugins
            .Where(p => p.Cell is not null && plan.Cells.Contains(p.Cell, StringComparer.Ordinal))
            .Select(p => state.Find(p.Id))
            .Where(p => p?.StagedChange?.Target is not null)
            .Select(p => $"{p!.Id} {p.Version} -> {p.StagedChange!.TargetVersion}")
            .ToList();
        return new InstallOutcome
        {
            Version = plan.Version,
            // A plugin dependency that was staged for uninstall is kept (its uninstall cancelled), not new.
            NewPlugins = plan.NewPlugins.Select(p => p.Id).Where(id => state.Find(id) is null || string.Equals(id, packageId, StringComparison.OrdinalIgnoreCase)).ToList(),
            Cells = plan.Cells,
            Plan = after,
            Issues = issues,
            StagedInCells = stagedInCells,
            UninstallCancelled = uninstalling is null
                ? null
                : $"{uninstalling.Id} was staged for uninstall; the install cancelled that, and it switches from {uninstalling.Version} to {plan.Version} at the next startup.",
        };
    }

    /// <summary>
    /// Installing a plugin that's staged for uninstall, at a version the request accepts: the staged uninstall is
    /// cancelled (and those of plugin dependencies it needs, uninstalled with it). Nothing is downloaded.
    /// </summary>
    private InstallOutcome CancelUninstall(RootLock rootLock, StateDocument state, StateDocument effective, PluginState uninstalling, List<ValidationIssue> issues)
    {
        var restored = new Dictionary<string, PluginState>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<PluginState>([uninstalling]);
        while (pending.TryDequeue(out var plugin))
        {
            if (!restored.TryAdd(plugin.Id, plugin))
            {
                continue;
            }

            foreach (var dependency in plugin.Dependencies.Where(d => d.Classification == DependencyClass.Plugin && effective.Find(d.Id) is null))
            {
                if (state.Find(dependency.Id) is { StagedChange.Kind: StagedChangeKind.Uninstall } other)
                {
                    pending.Enqueue(other);
                }
            }
        }

        var written = state with
        {
            Plugins = state.Plugins.Select(p => restored.ContainsKey(p.Id) ? p with { StagedChange = null } : p).ToList(),
        };
        var validator = new PluginValidator(host, layout, options.Cells, NullLoggerFactory.Instance);
        issues.AddRange(HotLoadRule.Added(validator.Validate(effective).Report, validator.Validate(StagedChanges.Effective(written)).Report));
        new StateStore(layout.Root).Write(rootLock, written);
        var others = restored.Keys.Where(id => !string.Equals(id, uninstalling.Id, StringComparison.OrdinalIgnoreCase)).ToList();
        return new InstallOutcome
        {
            AlreadyInstalled = uninstalling,
            Version = uninstalling.Version,
            Issues = issues,
            UninstallCancelled = $"{uninstalling.Id} was staged for uninstall; the install cancelled that, and it stays at {uninstalling.Version}"
                + (others.Count == 0 ? "." : $" (and so do its plugin dependencies {string.Join(", ", others)}).")
        };
    }

    /// <summary>
    /// Stages an update (Phase 5): under the root lock, re-reads the state file, plans the update on the state the next
    /// startup will load (<see cref="InstallPlanner.PlanUpdateAsync"/>: the cell and its dependents' cells resolved
    /// again), checks feeds and packages against the trust policy, downloads and extracts the new packages next to the
    /// old ones, records them, validates the new next-startup state and writes the switch as staged changes.
    /// </summary>
    public async Task<ChangeResult> UpdateAsync(string pluginId, VersionRange? range, bool includePrerelease, TimeSpan lockTimeout, IProgress<InstallProgress>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new InstallProgress(InstallStep.Resolving, pluginId));
        using var session = new FeedSession(feeds, verifier, logger, cancellationToken);
        var issues = session.FeedIssues.Select(f => ToIssue(f.Issue, null)).OfType<ValidationIssue>().ToList();
        using var rootLock = await RootLock.AcquireAsync(layout.Root, lockTimeout, cancellationToken).ConfigureAwait(false);

        var store = new StateStore(layout.Root);
        StateDocument current;
        try
        {
            current = store.Read();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return new ChangeResult(ChangeStatus.Failed, pluginId, null, [$"The state file can't be read: {ex.Message}"], [], issues);
        }

        var effective = StagedChanges.Effective(current);
        if (effective.Find(pluginId) is not { } existing)
        {
            var reason = current.Find(pluginId) is null ? $"{pluginId} isn't installed." : $"{pluginId} is staged for uninstall at the next startup.";
            return new ChangeResult(ChangeStatus.Failed, pluginId, null, [reason], [], issues);
        }

        InstallPlan plan;
        try
        {
            plan = await new InstallPlanner(host, layout, options, session, logger)
                .PlanUpdateAsync(effective, existing, range, includePrerelease, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (FeedException.Find(ex) is { } feedError)
        {
            return new ChangeResult(ChangeStatus.Failed, existing.Id, existing.Version, [feedError.Message], [], issues);
        }

        if (plan.NoChange is { } unchanged)
        {
            return new ChangeResult(ChangeStatus.Unchanged, existing.Id, existing.Version, [unchanged], [], issues);
        }

        if (plan.Failure is { } failure)
        {
            return new ChangeResult(failure == InstallStatus.Incompatible ? ChangeStatus.Incompatible : ChangeStatus.Failed,
                existing.Id, plan.Version ?? existing.Version, plan.Reasons, [], issues);
        }

        var recorder = new PackageRecorder(host, layout);
        if (await VerifyAndExtractAsync(plan, current, session, recorder, issues, progress, cancellationToken).ConfigureAwait(false) is { } rejected)
        {
            return new ChangeResult(ChangeStatus.Failed, existing.Id, plan.Version, [rejected], [], issues);
        }

        progress?.Report(new InstallProgress(InstallStep.Recording, existing.Id, plan.Version));
        var states = plan.Plugins
            .Select(p => Record(recorder, p))
            .Select(p => string.Equals(p.Id, existing.Id, StringComparison.OrdinalIgnoreCase) ? p with { Enabled = existing.Enabled } : p)
            .ToList();
        var desired = PackageRecorder.Merge(effective, states);
        var (result, write) = ChangeStager.Commit(host, layout, options, TimeProvider.System, current, effective, desired,
            new StageRequest(existing.Id, StagedChangeKind.Update, OthersReason: $"Resolved again for the update of {existing.Id} to {plan.Version}."),
            blockOnErrors: true, note: null, extraIssues: issues);
        if (write is not null)
        {
            store.Write(rootLock, write);
        }

        return result;
    }

    /// <summary>
    /// The trust policy on every package the plan uses, before anything is extracted: each downloaded package, and
    /// each package of a new or re-resolved plugin that's already in the plugin root (kept, orphaned, or shared with
    /// other plugins): its recorded source feed, and its kept <c>.nupkg</c> unless this policy accepted it before in
    /// this host (<see cref="TrustVerdicts"/>). Then extraction. Returns why the trust policy rejected a package, or <see langword="null"/>.
    /// </summary>
    private async Task<string?> VerifyAndExtractAsync(InstallPlan plan, StateDocument state, FeedSession session, PackageRecorder recorder, List<ValidationIssue> issues, IProgress<InstallProgress>? progress, CancellationToken cancellationToken)
    {
        foreach (var package in plan.Downloads)
        {
            var (id, version) = (package.Identity.Id, package.Identity.Version);
            FeedLog.Installing(logger, id, version, package.Source.PackageSource.Source);
            progress?.Report(new InstallProgress(InstallStep.Downloading, id, version));
            progress?.Report(new InstallProgress(InstallStep.VerifyingSignatures, id, version));
            var decision = await verdicts.VerifyDownloadAsync(verifier, package.NupkgPath, id, version, cancellationToken).ConfigureAwait(false);
            if (Rejection(logger, decision, id, version, issues, onDisk: false) is { } rejected)
            {
                return rejected;
            }
        }

        if (await VerifyKeptAsync(verifier, verdicts, layout, logger, state, KeptPackages(plan), issues, progress, cancellationToken).ConfigureAwait(false) is { } keptRejected)
        {
            return keptRejected;
        }

        foreach (var package in plan.Downloads)
        {
            progress?.Report(new InstallProgress(InstallStep.Extracting, package.Identity.Id, package.Identity.Version));
            await recorder.ExtractAsync(package.NupkgPath, package.Identity, package.Source.PackageSource.Source, session.NuGetLogger, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    /// <summary>
    /// The trust policy on packages already in the plugin root that a new plan uses (see <see cref="TrustVerdicts"/>),
    /// with the source feeds <paramref name="state"/> records (<see cref="RecordedSource"/>). Returns why it rejected
    /// one, or <see langword="null"/>.
    /// </summary>
    internal static async Task<string?> VerifyKeptAsync(
        IPackageVerifier verifier,
        TrustVerdicts verdicts,
        PackageLayout layout,
        ILogger logger,
        StateDocument state,
        IEnumerable<(string Id, NuGetVersion Version)> packages,
        List<ValidationIssue> issues,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        foreach (var (id, version) in packages)
        {
            var source = RecordedSource(state, layout, id, version);
            var decision = await verdicts.VerifyKeptAsync(verifier, layout, id, version, source, cancellationToken).ConfigureAwait(false);
            if (decision is null)
            {
                continue;   // accepted before by this policy
            }

            progress?.Report(new InstallProgress(InstallStep.VerifyingSignatures, id, version));
            if (Rejection(logger, decision, id, version, issues, onDisk: true) is { } rejected)
            {
                return rejected;
            }
        }

        return null;
    }

    /// <summary>
    /// The feed a package in the plugin root was downloaded from: as the state file records it (any version record of
    /// any plugin, or a cell-private dependency of one), else as its <c>.nupkg.metadata</c> records it (state files
    /// written before sources were recorded, and orphaned packages); <see langword="null"/> if neither does.
    /// </summary>
    internal static string? RecordedSource(StateDocument state, PackageLayout layout, string id, NuGetVersion version)
    {
        foreach (var record in state.Plugins.SelectMany(StagedChanges.VersionRecords))
        {
            if (string.Equals(record.Id, id, StringComparison.OrdinalIgnoreCase) && record.Version == version && record.Source is { Length: > 0 } own)
            {
                return own;
            }

            if (record.Dependencies.FirstOrDefault(d => d.Classification == DependencyClass.CellPrivate
                    && string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase) && d.Version == version && d.Source is { Length: > 0 }) is { } dependency)
            {
                return dependency.Source;
            }
        }

        return PackageRecorder.ReadSource(layout.GetNupkgMetadataPath(id, version));
    }

    /// <summary>The packages of the plan's new and re-resolved plugins (and their cell-private dependencies) that aren't downloaded.</summary>
    private static IEnumerable<(string Id, NuGetVersion Version)> KeptPackages(InstallPlan plan)
    {
        var downloads = plan.Downloads.Select(d => (d.Identity.Id.ToLowerInvariant(), d.Identity.Version)).ToHashSet();
        return plan.Plugins
            .SelectMany(p => p.Dependencies
                .Where(d => d.Classification == DependencyClass.CellPrivate)
                .Select(d => (d.Id, d.Version))
                .Prepend((p.Identity.Id, p.Identity.Version)))
            .DistinctBy(p => (p.Item1.ToLowerInvariant(), p.Item2))
            .Where(p => !downloads.Contains((p.Item1.ToLowerInvariant(), p.Item2)))
            .ToList();
    }

    /// <summary>Records a decision's issues; returns why it rejected the package, or <see langword="null"/>.</summary>
    private static string? Rejection(ILogger logger, TrustDecision decision, string id, NuGetVersion version, List<ValidationIssue> issues, bool onDisk)
    {
        foreach (var issue in decision.Issues)
        {
            if (ToIssue(issue, id) is { } mapped)
            {
                issues.Add(mapped);
            }

            if (issue.Severity == TrustIssueSeverity.Warning)
            {
                FeedLog.TrustWarning(logger, id, version, issue.Code, issue.Message);
            }
        }

        if (decision.Allowed)
        {
            return null;
        }

        var why = decision.Issues.Where(i => i.Severity == TrustIssueSeverity.Error).Select(i => $"[{i.Code}] {i.Message}").ToList();
        return $"The trust policy rejected {id} {version}{(onDisk ? " (already in the plugin root)" : "")}{(why.Count == 0 ? "." : ": " + string.Join(" ", why))}";
    }

    /// <summary>
    /// The state file entry of a planned plugin. A plugin that was installed before keeps its own file records,
    /// hash, source feed (when recorded), enabled flag and staged change, and the records of dependencies it already had: only what was resolved
    /// again is recorded again, so changes on disk are still detected.
    /// </summary>
    private static PluginState Record(PackageRecorder recorder, PluginToRecord plugin)
    {
        var dependencies = plugin.Dependencies
            .Select(d => d.Classification == DependencyClass.CellPrivate ? d with { Package = recorder.Record(d.Id, d.Version) } : d)
            .ToList();
        var recorded = recorder.CreatePluginState(recorder.Record(plugin.Identity.Id, plugin.Identity.Version), dependencies);
        if (plugin.Existing is not { } existing)
        {
            return recorded;
        }

        return recorded with
        {
            ContentHash = existing.ContentHash,
            Source = existing.Source ?? recorded.Source,
            Files = existing.Files,
            Enabled = existing.Enabled,
            PreviousVersions = existing.PreviousVersions,
            StagedChange = existing.StagedChange,
            StagedEnabled = existing.StagedEnabled,
            Dependencies = recorded.Dependencies.Select(d =>
                existing.Dependencies.FirstOrDefault(e => string.Equals(e.Id, d.Id, StringComparison.OrdinalIgnoreCase) && e.Version == d.Version && e.Classification == d.Classification) is { } kept
                    ? d with { ContentHash = kept.ContentHash, Source = kept.Source ?? d.Source, Files = kept.Files }
                    : d).ToList(),
        };
    }

    private static ValidationIssue? ToIssue(TrustIssue issue, string? packageId) => issue.Severity switch
    {
        TrustIssueSeverity.Error => new ValidationIssue(ValidationSeverity.Error, issue.Code, issue.Message, packageId),
        TrustIssueSeverity.Warning => new ValidationIssue(ValidationSeverity.Warning, issue.Code, issue.Message, packageId),
        _ => null,
    };
}

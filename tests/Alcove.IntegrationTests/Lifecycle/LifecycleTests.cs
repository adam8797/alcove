using Alcove.Cells;
using Alcove.Feeds;
using Alcove.Hosting;
using Alcove.Install;
using Alcove.IntegrationTests.Infrastructure;
using Alcove.Lifecycle;
using Alcove.Storage;
using Alcove.Validation;
using Fixture.HostContracts;
using Microsoft.Extensions.Logging.Abstractions;
using NuGet.Versioning;

namespace Alcove.IntegrationTests.Lifecycle;

/// <summary>
/// Staged changes, startup apply and cleanup, pending changes, update checks, restore and verification (Phase 5)
/// end to end, with Fixture.Lifecycle.Versioned 1.0/2.0/3.0 and Fixture.Lifecycle.User from a local folder feed. Each
/// "restart" is a new <see cref="PluginHost"/> on the same root; a host kept from before plays a second instance.
/// </summary>
public sealed class LifecycleTests(LifecycleFixtures lifecycle, HostingFixtures hosting) : IDisposable
{
    private const string Versioned = LifecycleFixtures.Versioned;
    private const string User = LifecycleFixtures.User;
    private const string Dep = "Fixture.Dep";
    private const string PackageType = "FixturePlugin";

    private static readonly VersionRange V1 = VersionRange.Parse("[1.0.0]");

    private readonly TempDirectory _root = new("alcove-lifecycle");
    private readonly TempDirectory _host = new("alcove-host");
    private readonly ListLoggerFactory _logs = new();
    private readonly List<PluginHost> _hosts = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (var host in _hosts)
        {
            host.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        _root.DisposeBestEffort();   // cells keep the root's assemblies loaded
        _host.Dispose();
    }

    /// <summary>The implementation plan's Phase 5 "Done when".</summary>
    [Fact]
    public async Task InstallUpdateRestartRollbackRestartUninstall_WithASecondInstanceSeeingPendingChanges()
    {
        // Install 1.0, hot.
        var first = await LoadedHostAsync();
        Assert.Equal(InstallStatus.Loaded, (await first.InstallAsync(Versioned, V1, cancellationToken: Ct)).Status);
        Assert.Equal("Versioned 1.0 uses Dep 1.0 in 'Fixture.Lifecycle.Versioned'", Describe(first, Versioned));
        var second = await LoadedHostAsync();   // another instance of the app on the same root
        Assert.Empty(await second.GetPendingChangesAsync(Ct));

        // Update: downloaded and recorded now, staged until restart; nothing loaded changes.
        var update = await first.UpdateAsync(Versioned, Ct);

        Assert.True(update.Status == ChangeStatus.Staged, update.ToString());
        Assert.Equal(NuGetVersion.Parse("3.0.0"), update.Version);
        var staged = Assert.Single(update.Changes);
        Assert.Equal((PendingChangeKind.Update, "1.0.0", "3.0.0", true), (staged.Kind, staged.CurrentVersion!.ToNormalizedString(), staged.NewVersion!.ToNormalizedString(), staged.IsStaged));
        Assert.Equal("Versioned 1.0 uses Dep 1.0 in 'Fixture.Lifecycle.Versioned'", Describe(first, Versioned));
        var state = new StateStore(_root.Path).Read().Find(Versioned)!;
        Assert.Equal(("1.0.0", StagedChangeKind.Update, "3.0.0"), (state.Version.ToNormalizedString(), state.StagedChange!.Kind, state.StagedChange.TargetVersion!.ToNormalizedString()));
        Assert.True(Directory.Exists(PackageFolder(Versioned, "3.0.0")));
        Assert.True(Directory.Exists(PackageFolder(Dep, "3.0.0")));
        AssertSingleChange(await first.GetPendingChangesAsync(Ct), PendingChangeKind.Update, "1.0.0", "3.0.0", isStaged: true);
        AssertSingleChange(await second.GetPendingChangesAsync(Ct), PendingChangeKind.Update, "1.0.0", "3.0.0", isStaged: true);

        // Restart: the update applies before validation; 1.0 is kept for rollback.
        var afterUpdate = await LoadedHostAsync();
        Assert.Equal("Versioned 3.0 uses Dep 3.0 in 'Fixture.Lifecycle.Versioned'", Describe(afterUpdate, Versioned));
        state = new StateStore(_root.Path).Read().Find(Versioned)!;
        Assert.Equal(("3.0.0", null), (state.Version.ToNormalizedString(), state.StagedChange));
        Assert.Equal(["1.0.0"], state.PreviousVersions.Select(p => p.Version.ToNormalizedString()));
        Assert.True(Directory.Exists(PackageFolder(Versioned, "1.0.0")));
        Assert.True(Directory.Exists(PackageFolder(Dep, "1.0.0")));
        Assert.Empty(await afterUpdate.GetPendingChangesAsync(Ct));
        AssertSingleChange(await second.GetPendingChangesAsync(Ct), PendingChangeKind.Update, "1.0.0", "3.0.0", isStaged: false);

        // Rollback, restart.
        var rollback = await afterUpdate.RollbackAsync(Versioned, Ct);
        Assert.True(rollback.Status == ChangeStatus.Staged, rollback.ToString());
        Assert.Equal(NuGetVersion.Parse("1.0.0"), rollback.Version);
        Assert.Equal(PendingChangeKind.Rollback, Assert.Single(rollback.Changes).Kind);
        AssertSingleChange(await afterUpdate.GetPendingChangesAsync(Ct), PendingChangeKind.Rollback, "3.0.0", "1.0.0", isStaged: true);

        var afterRollback = await LoadedHostAsync();
        Assert.Equal("Versioned 1.0 uses Dep 1.0 in 'Fixture.Lifecycle.Versioned'", Describe(afterRollback, Versioned));
        Assert.Equal(["3.0.0"], new StateStore(_root.Path).Read().Find(Versioned)!.PreviousVersions.Select(p => p.Version.ToNormalizedString()));

        // Uninstall, restart: gone from the state file, and its packages are deleted after the start.
        var uninstall = await afterRollback.UninstallAsync(Versioned, cascade: false, Ct);
        Assert.True(uninstall.Status == ChangeStatus.Staged, uninstall.ToString());
        Assert.Contains(await second.GetPendingChangesAsync(Ct), c => c.Kind == PendingChangeKind.Uninstall && c.IsStaged);
        Assert.Equal(PluginStatus.Loaded, Assert.Single(afterRollback.Plugins).Status);

        var afterUninstall = await LoadedHostAsync();
        Assert.Empty(afterUninstall.Plugins);
        Assert.Empty(new StateStore(_root.Path).Read().Plugins);
        AssertSingleChange(await second.GetPendingChangesAsync(Ct), PendingChangeKind.Uninstall, "1.0.0", null, isStaged: false);
        Assert.True(Directory.Exists(PackageFolder(Versioned, "1.0.0")), "A package other running hosts use was deleted.");

        // Once the other instances are gone, the next start deletes the packages.
        foreach (var running in new[] { first, second, afterUpdate, afterRollback })
        {
            await running.DisposeAsync();
        }

        await LoadedHostAsync();
        if (!OperatingSystem.IsWindows())
        {
            // Windows keeps the assemblies this process loaded locked: deleted at a later start.
            Assert.False(Directory.Exists(Path.Combine(_root.Path, "packages", Versioned.ToLowerInvariant())));
            Assert.False(Directory.Exists(Path.Combine(_root.Path, "packages", Dep.ToLowerInvariant())));
        }
    }

    [Fact]
    public async Task UninstallingAPluginOthersDependOn_IsBlocked_UnlessCascade()
    {
        var host = await LoadedHostAsync();
        var installed = await host.InstallAsync(User, cancellationToken: Ct);
        Assert.Equal([Versioned, User], installed.Plugins.Select(p => p.Id));

        var blocked = await host.UninstallAsync(Versioned, cancellationToken: Ct);

        Assert.Equal(ChangeStatus.Blocked, blocked.Status);
        Assert.Contains($"{User} depends on {Versioned}.", blocked.Reasons);
        Assert.Contains("cascade", blocked.Reason);
        Assert.Empty(blocked.Changes);
        Assert.All(new StateStore(_root.Path).Read().Plugins, p => Assert.Null(p.StagedChange));

        var cascade = await host.UninstallAsync(Versioned, cascade: true, Ct);

        Assert.True(cascade.Status == ChangeStatus.Staged, cascade.ToString());
        Assert.Equal([(Versioned, PendingChangeKind.Uninstall), (User, PendingChangeKind.Uninstall)], cascade.Changes.Select(c => (c.PluginId, c.Kind)));
        Assert.Contains($"Uninstalled with {Versioned} (cascade)", cascade.Changes[1].Reason);
        Assert.Equal(ChangeStatus.Unchanged, (await host.UninstallAsync(User, cancellationToken: Ct)).Status);
        Assert.All(host.Plugins, p => Assert.Equal(PluginStatus.Loaded, p.Status));   // restart-only

        var restarted = await LoadedHostAsync();
        Assert.Empty(restarted.Plugins);
        Assert.Empty(new StateStore(_root.Path).Read().Plugins);
    }

    [Fact]
    public async Task UninstallingAPluginNothingDependsOn_IsStaged_AndItsDependencyStays()
    {
        var host = await LoadedHostAsync();
        await host.InstallAsync(User, cancellationToken: Ct);

        var result = await host.UninstallAsync(User, cancellationToken: Ct);

        Assert.True(result.Status == ChangeStatus.Staged, result.ToString());
        Assert.Equal([User], result.Changes.Select(c => c.PluginId));
        var restarted = await LoadedHostAsync();
        Assert.Equal([Versioned], restarted.Plugins.Select(p => p.Id));
        Assert.True(Directory.Exists(PackageFolder(Versioned, "1.0.0")));
        Assert.Equal(ChangeStatus.Failed, (await restarted.UninstallAsync(User, cancellationToken: Ct)).Status);
    }

    [Fact]
    public async Task DisableAndEnable_AreStagedUntilRestart()
    {
        var host = await LoadedHostAsync();
        await host.InstallAsync(Versioned, V1, cancellationToken: Ct);

        var disable = await host.DisableAsync(Versioned, Ct);

        Assert.True(disable.Status == ChangeStatus.Staged, disable.ToString());
        Assert.Equal(PendingChangeKind.Disable, Assert.Single(disable.Changes).Kind);
        Assert.Equal(ChangeStatus.Unchanged, (await host.DisableAsync(Versioned, Ct)).Status);
        Assert.Equal(PluginStatus.Loaded, Assert.Single(host.Plugins).Status);
        AssertSingleChange(await host.GetPendingChangesAsync(Ct), PendingChangeKind.Disable, "1.0.0", "1.0.0", isStaged: true);

        var disabled = await LoadedHostAsync();
        Assert.Equal(PluginStatus.Disabled, Assert.Single(disabled.Plugins).Status);
        Assert.Empty(disabled.Catalog.GetAll<IDescriber>());
        Assert.False(new StateStore(_root.Path).Read().Find(Versioned)!.Enabled);

        // Enabling then disabling again before the restart stages nothing.
        Assert.Equal(ChangeStatus.Staged, (await disabled.EnableAsync(Versioned, Ct)).Status);
        var undo = await disabled.DisableAsync(Versioned, Ct);
        Assert.Equal(ChangeStatus.Staged, undo.Status);
        Assert.Empty(undo.Changes);
        Assert.Null(new StateStore(_root.Path).Read().Find(Versioned)!.StagedEnabled);

        Assert.Equal(ChangeStatus.Staged, (await disabled.EnableAsync(Versioned, Ct)).Status);
        var enabled = await LoadedHostAsync();
        Assert.Equal(PluginStatus.Loaded, Assert.Single(enabled.Plugins).Status);
        Assert.Equal("Versioned 1.0 uses Dep 1.0 in 'Fixture.Lifecycle.Versioned'", Describe(enabled, Versioned));
    }

    [Fact]
    public async Task DisablingAPluginOthersDependOn_IsStaged_AndSaysTheyWontLoad()
    {
        var host = await LoadedHostAsync();
        await host.InstallAsync(User, cancellationToken: Ct);

        var result = await host.DisableAsync(Versioned, Ct);

        Assert.True(result.Status == ChangeStatus.Staged, result.ToString());
        Assert.Contains(result.Issues, i => i.Code == ValidationCodes.PluginDependencyDisabled && i.PluginId == User);
        Assert.Contains("At the next startup", result.Reason);
    }

    [Fact]
    public async Task RollbackWithoutAPreviousVersion_FailsClearly_AndRollingBackAStagedUpdateCancelsIt()
    {
        var host = await LoadedHostAsync();
        await host.InstallAsync(Versioned, V1, cancellationToken: Ct);

        var none = await host.RollbackAsync(Versioned, Ct);
        Assert.Equal(ChangeStatus.Failed, none.Status);
        Assert.Contains("No previous version", none.Reason);
        Assert.Equal(ChangeStatus.Failed, (await host.RollbackAsync("Fixture.Nope", Ct)).Status);

        Assert.Equal(ChangeStatus.Staged, (await host.UpdateAsync(Versioned, VersionRange.Parse("[2.0.0]"), cancellationToken: Ct)).Status);
        var cancel = await host.RollbackAsync(Versioned, Ct);

        Assert.True(cancel.Status == ChangeStatus.Staged, cancel.ToString());
        Assert.Contains("cancelled", cancel.Reason);
        Assert.Null(new StateStore(_root.Path).Read().Find(Versioned)!.StagedChange);
        Assert.Empty(await host.GetPendingChangesAsync(Ct));
    }

    [Fact]
    public async Task UpdatingWhenNothingNewerExists_IsUnchanged()
    {
        var host = await LoadedHostAsync();
        await host.InstallAsync(Versioned, cancellationToken: Ct);   // 3.0, the newest

        var result = await host.UpdateAsync(Versioned, Ct);

        Assert.Equal(ChangeStatus.Unchanged, result.Status);
        Assert.Contains("newest", result.Reason);
        Assert.Equal(ChangeStatus.Failed, (await host.UpdateAsync("Fixture.Nope", Ct)).Status);
    }

    [Fact]
    public async Task Cleanup_KeepsVersionsToKeepPreviousVersions_AndDeletesOlderOnesWithTheirOrphanedDependencies()
    {
        var host = await LoadedHostAsync(o => o.VersionsToKeep = 1);
        await host.InstallAsync(Versioned, V1, cancellationToken: Ct);
        await host.UpdateAsync(Versioned, VersionRange.Parse("[2.0.0]"), cancellationToken: Ct);

        var second = await LoadedHostAsync(o => o.VersionsToKeep = 1);
        Assert.Equal("Versioned 2.0 uses Dep 2.0 in 'Fixture.Lifecycle.Versioned'", Describe(second, Versioned));
        Assert.Equal(ChangeStatus.Staged, (await second.UpdateAsync(Versioned, Ct)).Status);

        await host.DisposeAsync();   // the only host using 1.0
        var third = await LoadedHostAsync(o => o.VersionsToKeep = 1);

        Assert.Equal("Versioned 3.0 uses Dep 3.0 in 'Fixture.Lifecycle.Versioned'", Describe(third, Versioned));
        var state = new StateStore(_root.Path).Read().Find(Versioned)!;
        Assert.Equal(["2.0.0"], state.PreviousVersions.Select(p => p.Version.ToNormalizedString()));
        Assert.All(new[] { (Versioned, "2.0.0"), (Versioned, "3.0.0"), (Dep, "2.0.0"), (Dep, "3.0.0") }, p => Assert.True(Directory.Exists(PackageFolder(p.Item1, p.Item2)), $"{p} was deleted."));
        if (!OperatingSystem.IsWindows())
        {
            // Windows keeps 1.0 locked (this process loaded it) until a later start.
            Assert.False(Directory.Exists(PackageFolder(Versioned, "1.0.0")), "The version beyond VersionsToKeep wasn't deleted.");
            Assert.False(Directory.Exists(PackageFolder(Dep, "1.0.0")), "The orphaned dependency wasn't deleted.");
        }

        Assert.Empty((await third.ValidateAsync(Ct)).Issues);
    }

    [Fact]
    public async Task Cleanup_NeverDeletesWhatALoadedCellUses()
    {
        var host = await LoadedHostAsync(o => o.VersionsToKeep = 0);
        await host.InstallAsync(Versioned, V1, cancellationToken: Ct);
        Assert.Equal(ChangeStatus.Staged, (await host.UpdateAsync(Versioned, Ct)).Status);
        var orphan = Directory.CreateDirectory(PackageFolder("Fixture.Orphan", "1.0.0")).FullName;
        File.WriteAllText(Path.Combine(orphan, "leftover.txt"), "from an interrupted install");

        // Another instance starts: it applies the update and keeps no previous version.
        await new StartupMaintenance(new PackageLayout(_root.Path), 0, NullLogger.Instance).ApplyStagedChangesAsync(TimeSpan.FromSeconds(5), _ => { }, Ct);
        var state = new StateStore(_root.Path).Read().Find(Versioned)!;
        Assert.Equal(("3.0.0", 0), (state.Version.ToNormalizedString(), state.PreviousVersions.Count));

        host.DeleteUnusedPackages();

        Assert.True(Directory.Exists(PackageFolder(Versioned, "1.0.0")), "A package this process loaded was deleted.");
        Assert.True(Directory.Exists(PackageFolder(Dep, "1.0.0")), "A dependency this process loaded was deleted.");
        Assert.True(Directory.Exists(PackageFolder(Versioned, "3.0.0")));
        Assert.False(Directory.Exists(orphan), "An unused package folder wasn't deleted.");
        Assert.Equal("Versioned 1.0 uses Dep 1.0 in 'Fixture.Lifecycle.Versioned'", Describe(host, Versioned));
    }

    [Fact]
    public async Task RestoreAsync_DownloadsEverythingTheStateFilePins_AfterPackagesWasDeleted()
    {
        Assert.Equal(InstallStatus.Installed, (await Create().InstallAsync(User, cancellationToken: Ct)).Status);
        Directory.Delete(Path.Combine(_root.Path, "packages"), recursive: true);
        var host = Create();
        Assert.Contains((await host.ValidateAsync(Ct)).Errors, e => e.Code == ValidationCodes.PackageMissing);
        var steps = new List<InstallProgress>();

        var result = await host.RestoreAsync(new SyncProgress<InstallProgress>(steps.Add), Ct);

        Assert.True(result.Succeeded, result.ToString());
        Assert.Equal([(Dep, "1.0.0"), (User, "1.0.0"), (Versioned, "1.0.0")], result.Packages.Select(p => (p.PackageId, p.Version.ToNormalizedString())).Order());
        Assert.Contains(steps, s => s.Step == InstallStep.VerifyingSignatures);
        Assert.Empty((await host.ValidateAsync(Ct)).Issues);
        Assert.False((await host.VerifyAsync(Ct)).HasErrors);
        await host.LoadAsync(Ct);
        Assert.Equal("User sees Versioned 1.0", Describe(host, User));

        Assert.Empty((await host.RestoreAsync(Ct)).Packages);
    }

    [Fact]
    public async Task RestoreAsync_RefusesAPackageWhoseContentHashIsntThePinnedOne()
    {
        await Create().InstallAsync(Versioned, V1, cancellationToken: Ct);
        var state = new StateStore(_root.Path).Read();
        var pinned = state.Find(Versioned)!;
        await WriteStateAsync(PackageRecorderMerge(state, pinned with { ContentHash = Convert.ToBase64String(new byte[64]) }));
        Directory.Delete(PackageFolder(Versioned, "1.0.0"), recursive: true);

        var result = await Create().RestoreAsync(Ct);

        Assert.False(result.Succeeded);
        var failed = Assert.Single(result.Packages);
        Assert.Equal((Versioned, false), (failed.PackageId, failed.Restored));
        Assert.Contains("contentHash", failed.Reason);
        Assert.Contains("isn't the package that was installed", failed.Reason);
        Assert.False(Directory.Exists(PackageFolder(Versioned, "1.0.0")), "A package with the wrong hash was extracted.");
    }

    [Fact]
    public async Task RollbackAsync_AppliesTheCurrentTrustPolicyToTheKeptVersion()
    {
        // Review finding 4: the kept version was accepted when it was installed; the policy may have changed since.
        var first = await LoadedHostAsync();
        Assert.Equal(InstallStatus.Loaded, (await first.InstallAsync(Versioned, V1, cancellationToken: Ct)).Status);
        Assert.Equal(ChangeStatus.Staged, (await first.UpdateAsync(Versioned, Ct)).Status);
        var host = await LoadedHostAsync();   // the update applies; 1.0 is kept
        host.Verifier = new RejectingVerifier(Dep);

        var result = await host.RollbackAsync(Versioned, Ct);

        Assert.Equal(ChangeStatus.Failed, result.Status);
        Assert.Contains("The trust policy rejected Fixture.Dep 1.0.0 (already in the plugin root): [TEST001] not trusted", result.Reason);
        Assert.Contains(result.Issues, i => i.Code == "TEST001");
        Assert.Null(new StateStore(_root.Path).Read().Find(Versioned)!.StagedChange);

        host.Verifier = new RecordingVerifier();
        Assert.Equal(ChangeStatus.Staged, (await host.RollbackAsync(Versioned, Ct)).Status);
    }

    [Fact]
    public async Task RestoreAsync_AppliesTheTrustPolicy()
    {
        await Create().InstallAsync(Versioned, V1, cancellationToken: Ct);
        Directory.Delete(Path.Combine(_root.Path, "packages"), recursive: true);
        var host = Create();
        host.Verifier = new RejectingVerifier(Dep);

        var result = await host.RestoreAsync(Ct);

        Assert.False(result.Succeeded);
        Assert.Contains("[TEST001] not trusted", result.Packages.Single(p => p.PackageId == Dep).Reason);
        Assert.True(result.Packages.Single(p => p.PackageId == Versioned).Restored);
        Assert.Contains(result.Issues, i => i.Code == "TEST001" && i.PluginId == Dep);
        Assert.False(Directory.Exists(PackageFolder(Dep, "1.0.0")));
    }

    [Fact]
    public async Task VerifyAsync_RehashesEveryFile_AndFindsAModificationTheStartupCheckCantSee()
    {
        await Create().InstallAsync(Versioned, V1, cancellationToken: Ct);
        var host = Create();
        var clean = await host.VerifyAsync(Ct);
        Assert.True(clean.Issues.Count == 0, clean.ToString());
        Assert.Equal(2, clean.PackagesChecked);
        Assert.True(clean.FilesChecked >= 4, clean.ToString());

        // Same size, same timestamp, different content.
        var dll = Path.Combine(PackageFolder(Versioned, "1.0.0"), "lib", "net10.0", "Fixture.Lifecycle.Versioned.dll");
        var timestamp = File.GetLastWriteTimeUtc(dll);
        var bytes = File.ReadAllBytes(dll);
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(dll, bytes);
        File.SetLastWriteTimeUtc(dll, timestamp);
        Assert.Empty((await host.ValidateAsync(Ct)).Issues);

        var verifier = new RecordingVerifier();
        host.Verifier = verifier;
        var report = await host.VerifyAsync(verifySignatures: true, Ct);

        Assert.True(report.HasErrors);
        var error = Assert.Single(report.Issues);
        Assert.Equal((ValidationCodes.FileContentChanged, Versioned), (error.Code, error.PluginId));
        Assert.Contains("lib/net10.0/Fixture.Lifecycle.Versioned.dll", error.Message);
        Assert.Equal([Dep, Versioned], verifier.Packages.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task VerifyAsync_ReportsAChangedNupkgAndAnAddedFile()
    {
        await Create().InstallAsync(Versioned, V1, cancellationToken: Ct);
        var folder = PackageFolder(Dep, "1.0.0");
        File.WriteAllText(Path.Combine(folder, "lib", "net10.0", "Extra.dll"), "not from the package");
        var state = new StateStore(_root.Path).Read();
        await WriteStateAsync(PackageRecorderMerge(state, state.Find(Versioned)! with { ContentHash = Convert.ToBase64String(new byte[64]) }));

        var report = await Create().VerifyAsync(Ct);

        Assert.Contains(report.Issues, i => i.Code == ValidationCodes.ContentHashMismatch && i.PluginId == Versioned);
        Assert.Contains(report.Issues, i => i.Code == ValidationCodes.UnexpectedFile && i.PluginId == Dep && i.Message.Contains("lib/net10.0/Extra.dll", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CheckForUpdatesAsync_FindsTheNewestVersion()
    {
        await Create().InstallAsync(Versioned, V1, cancellationToken: Ct);

        var update = Assert.Single(await Create().CheckForUpdatesAsync(Ct));

        Assert.Equal((Versioned, "1.0.0", "3.0.0"), (update.PluginId, update.InstalledVersion.ToNormalizedString(), update.AvailableVersion?.ToNormalizedString()));
        Assert.Empty(update.IncompatibleVersions);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_ReportsANewerVersionTheHostCantRun_AndUpdateSkipsIt()
    {
        // The host ships Fixture.Dep 2.0 as its own: Versioned 3.0 (Dep >= 3.0) can't run here.
        var environment = FabricatedHost.With(_host, (Dep, "2.0.0", "2.0.0.0"));
        void HostOwnedDep(PluginHostOptions o) => o.HostOwnedPackages.Add(Dep);
        Assert.Equal(InstallStatus.Installed, (await Create(HostOwnedDep, environment).InstallAsync(Versioned, V1, cancellationToken: Ct)).Status);
        var host = Create(HostOwnedDep, environment);

        var update = Assert.Single(await host.CheckForUpdatesAsync(Ct));

        Assert.Equal("2.0.0", update.AvailableVersion?.ToNormalizedString());
        var incompatible = Assert.Single(update.IncompatibleVersions);
        Assert.Equal("3.0.0", incompatible.Version.ToNormalizedString());
        Assert.Contains("Fixture.Dep (>= 3.0.0), but this host has 2.0.0", incompatible.Reason);

        var result = await host.UpdateAsync(Versioned, Ct);
        Assert.True(result.Status == ChangeStatus.Staged, result.ToString());
        Assert.Equal(NuGetVersion.Parse("2.0.0"), result.Version);

        var exact = await host.UpdateAsync(Versioned, VersionRange.Parse("[3.0.0]"), cancellationToken: Ct);
        Assert.Equal(ChangeStatus.Incompatible, exact.Status);
        Assert.Contains("this host has 2.0.0", exact.Reason);
    }

    [Fact]
    public async Task UpdatesAreConstrainedByTheRangesOtherPluginsDeclare()
    {
        var host = await LoadedHostAsync();
        await host.InstallAsync(User, cancellationToken: Ct);   // Versioned 1.0 (the lowest applicable) and User

        var updates = await host.CheckForUpdatesAsync(Ct);

        var versioned = updates.Single(u => u.PluginId == Versioned);
        Assert.Equal("2.0.0", versioned.AvailableVersion?.ToNormalizedString());
        var blocked = Assert.Single(versioned.IncompatibleVersions);
        Assert.Equal("3.0.0", blocked.Version.ToNormalizedString());
        Assert.Contains($"{User} needs {Versioned} (>= 1.0.0 && < 3.0.0)", blocked.Reason);
        Assert.False(updates.Single(u => u.PluginId == User).IsUpdateAvailable);

        var tooNew = await host.UpdateAsync(Versioned, VersionRange.Parse("[3.0.0]"), cancellationToken: Ct);
        Assert.Equal(ChangeStatus.Failed, tooNew.Status);
        Assert.Contains($"{User} needs {Versioned}", tooNew.Reason);

        var update = await host.UpdateAsync(Versioned, Ct);
        Assert.True(update.Status == ChangeStatus.Staged, update.ToString());
        Assert.Equal(NuGetVersion.Parse("2.0.0"), update.Version);
        Assert.Contains(update.Changes, c => c.PluginId == User && c.Kind == PendingChangeKind.Update && c.Reason!.Contains("Resolved again", StringComparison.Ordinal));
        Assert.Equal("User sees Versioned 1.0", Describe(host, User));

        var restarted = await LoadedHostAsync();
        Assert.Equal("User sees Versioned 2.0", Describe(restarted, User));
        Assert.Empty((await restarted.ValidateAsync(Ct)).Issues);
    }

    [Fact]
    public async Task StagedChanges_WaitForTheRootLock_AndTimeOut()
    {
        var host = await LoadedHostAsync();
        await host.InstallAsync(Versioned, V1, cancellationToken: Ct);
        var rootLock = await RootLock.AcquireAsync(_root.Path, cancellationToken: Ct);

        var disable = host.DisableAsync(Versioned, Ct);
        await Task.Delay(TimeSpan.FromMilliseconds(500), Ct);
        Assert.False(disable.IsCompleted, "The change was staged while another process held the root lock.");
        rootLock.Dispose();
        Assert.Equal(ChangeStatus.Staged, (await disable).Status);

        using (await RootLock.AcquireAsync(_root.Path, cancellationToken: Ct))
        {
            host.LockTimeout = TimeSpan.FromMilliseconds(200);
            await Assert.ThrowsAsync<TimeoutException>(() => host.UninstallAsync(Versioned, cancellationToken: Ct));
            await Assert.ThrowsAsync<TimeoutException>(() => host.UpdateAsync(Versioned, Ct));

            // A startup doesn't wait forever either: it loads what's active and applies the staged change next time.
            var blocked = Create();
            blocked.LockTimeout = TimeSpan.FromMilliseconds(200);
            await blocked.LoadAsync(Ct);
            Assert.Equal(PluginStatus.Loaded, Assert.Single(blocked.Plugins).Status);
            Assert.Single(_logs.WithEventId(5002));   // LifecycleLog.ApplySkipped
        }

        var state = new StateStore(_root.Path).Read().Find(Versioned)!;
        Assert.Equal((false, (StagedChange?)null), (state.StagedEnabled, state.StagedChange));
        var restarted = await LoadedHostAsync();
        Assert.Equal(PluginStatus.Disabled, Assert.Single(restarted.Plugins).Status);
    }

    [Fact]
    public async Task APluginWhoseMissingDependencyWasInstalledSince_WillLoadAtTheNextStartup()
    {
        await Create().InstallAsync(User, cancellationToken: Ct);
        var state = new StateStore(_root.Path).Read();
        await WriteStateAsync(state with { Plugins = state.Plugins.Where(p => p.Id != Versioned).ToList() });
        var host = await LoadedHostAsync();
        Assert.Equal(PluginStatus.NotLoaded, Assert.Single(host.Plugins).Status);

        Assert.Equal(InstallStatus.Loaded, (await host.InstallAsync(Versioned, V1, cancellationToken: Ct)).Status);

        Assert.Equal(PluginStatus.NotLoaded, host.Plugins.Single(p => p.Id == User).Status);   // never re-activated (ADR 0011)
        var change = Assert.Single(await host.GetPendingChangesAsync(Ct));
        Assert.Equal((PendingChangeKind.WillLoad, User, false), (change.Kind, change.PluginId, change.IsStaged));
        Assert.Contains(Versioned, change.Reason);

        var restarted = await LoadedHostAsync();
        Assert.Equal("User sees Versioned 1.0", Describe(restarted, User));
    }

    [Fact]
    public async Task PendingChanges_ListHotInstallsPendingRestart_AndInstallsByOtherInstances()
    {
        const string UserB = "Fixture.Hosting.UserB";
        void SharedCell(PluginHostOptions o)
        {
            o.Feeds.Add(new PackageFeed(lifecycle.PackagesDirectory));
            o.Feeds.Add(new PackageFeed(hosting.PackagesDirectory));
            o.Cells.Add(new CellDefinition("Shared") { Plugins = { Versioned, UserB } });
        }

        var host = await LoadedHostAsync(SharedCell);
        Assert.Equal(InstallStatus.Loaded, (await host.InstallAsync(Versioned, V1, cancellationToken: Ct)).Status);   // the cell commits to Dep 1.0
        var pending = await host.InstallAsync(UserB, cancellationToken: Ct);   // needs Dep >= 2.0 in the same cell
        Assert.Equal(InstallStatus.PendingRestart, pending.Status);
        Assert.Equal(InstallStatus.Installed, (await Create(SharedCell).InstallAsync("Fixture.Hosting.UserA", cancellationToken: Ct)).Status);

        var changes = await host.GetPendingChangesAsync(Ct);

        var restart = changes.Single(c => c.PluginId == UserB);
        Assert.Equal((PendingChangeKind.PendingRestart, false), (restart.Kind, restart.IsStaged));
        Assert.Equal(pending.Reasons, restart.Reasons);
        var other = changes.Single(c => c.PluginId == "Fixture.Hosting.UserA");
        Assert.Equal((PendingChangeKind.Install, false), (other.Kind, other.IsStaged));
        Assert.Equal(2, changes.Count);
    }

    [Fact]
    public async Task Cleanup_KeepsWhatAnotherRunningHostUses_UntilItIsDisposed()
    {
        // Host 1 runs 1.0; host 2 (another instance) updates with VersionsToKeep = 0 and restarts.
        void KeepNone(PluginHostOptions o) => o.VersionsToKeep = 0;
        var first = await LoadedHostAsync(KeepNone);
        await first.InstallAsync(Versioned, V1, cancellationToken: Ct);
        Assert.True(File.Exists(first.InUseRecordPath), "The host wrote no in-use record.");
        var second = await LoadedHostAsync(KeepNone);
        Assert.Equal(ChangeStatus.Staged, (await second.UpdateAsync(Versioned, Ct)).Status);

        var restarted = await LoadedHostAsync(KeepNone);

        Assert.Equal("Versioned 3.0 uses Dep 3.0 in 'Fixture.Lifecycle.Versioned'", Describe(restarted, Versioned));
        Assert.Empty(new StateStore(_root.Path).Read().Find(Versioned)!.PreviousVersions);
        Assert.True(Directory.Exists(PackageFolder(Versioned, "1.0.0")), "A package another running host uses was deleted.");
        Assert.True(Directory.Exists(PackageFolder(Dep, "1.0.0")), "A dependency another running host uses was deleted.");

        var record = first.InUseRecordPath!;
        await first.DisposeAsync();
        await second.DisposeAsync();
        Assert.False(File.Exists(record), "Dispose left the in-use record.");

        restarted.DeleteUnusedPackages();   // the cleanup of a later start
        if (!OperatingSystem.IsWindows())
        {
            // Windows keeps 1.0 locked (this process loaded it) until a later start.
            Assert.False(Directory.Exists(PackageFolder(Versioned, "1.0.0")));
            Assert.False(Directory.Exists(PackageFolder(Dep, "1.0.0")));
        }

        Assert.True(Directory.Exists(PackageFolder(Versioned, "3.0.0")));
    }

    [Fact]
    public void InUseRecords_CountOnlyLiveProcesses_CheckingTheStartTime_AndRemoveStaleOnes()
    {
        var layout = new PackageLayout(_root.Path);
        var directory = Path.Combine(_root.Path, InUseRecords.DirectoryName);
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        var started = self.StartTime.ToUniversalTime();
        (string, NuGetVersion)[] Package(string version) => [(Versioned, NuGetVersion.Parse(version))];
        InUseRecords.Write(Path.Combine(directory, "live.json"), self.Id, started, Package("1.0.0"));
        InUseRecords.Write(Path.Combine(directory, "reused-pid.json"), self.Id, started.AddHours(-1), Package("2.0.0"));
        InUseRecords.Write(Path.Combine(directory, "dead.json"), DeadProcessId(), started, Package("3.0.0"));
        File.WriteAllText(Path.Combine(directory, $"{DeadProcessId()}-0-crashed.json"), "{ truncated");

        var inUse = InUseRecords.ReadLive(layout, _ => { });

        Assert.Equal([Path.GetFullPath(PackageFolder(Versioned, "1.0.0"))], inUse);
        Assert.Equal(["live.json"], Directory.GetFiles(directory).Select(Path.GetFileName));

        // An unreadable record of a live process blocks the cleanup rather than risk a deletion.
        File.WriteAllText(Path.Combine(directory, $"{self.Id}-0-writing.json"), "{ truncated");
        Assert.Null(InUseRecords.ReadLive(layout, _ => { }));
    }

    [Fact]
    public async Task AnInstallIntoACellWithAStagedSwitch_ResolvesTheStagedVersion_AndIsPendingRestart()
    {
        const string UserB = "Fixture.Hosting.UserB";
        void SharedCell(PluginHostOptions o)
        {
            o.Feeds.Add(new PackageFeed(lifecycle.PackagesDirectory));
            o.Feeds.Add(new PackageFeed(hosting.PackagesDirectory));
            o.Cells.Add(new CellDefinition("Shared") { Plugins = { Versioned, UserB } });
        }

        // Versioned runs 2.0 (Dep 2.0) and is staged to roll back to 1.0 (Dep 1.0).
        var first = await LoadedHostAsync(SharedCell);
        await first.InstallAsync(Versioned, V1, cancellationToken: Ct);
        await first.UpdateAsync(Versioned, VersionRange.Parse("[2.0.0]"), cancellationToken: Ct);
        var host = await LoadedHostAsync(SharedCell);
        Assert.Equal(ChangeStatus.Staged, (await host.RollbackAsync(Versioned, Ct)).Status);

        // UserB (Dep >= 2.0) joins the cell: resolved with Versioned 1.0, the cell needs Dep 2.0 for both.
        var result = await host.InstallAsync(UserB, cancellationToken: Ct);

        Assert.Equal(InstallStatus.PendingRestart, result.Status);
        Assert.Contains($"changes staged for the next startup ({Versioned} 2.0.0 -> 1.0.0)", result.Reason);
        var state = new StateStore(_root.Path).Read().Find(Versioned)!;
        Assert.Equal(("2.0.0", StagedChangeKind.Rollback, "1.0.0"), (state.Version.ToNormalizedString(), state.StagedChange!.Kind, state.StagedChange.TargetVersion!.ToNormalizedString()));
        Assert.Equal("2.0.0", state.StagedChange.Target!.Dependencies.Single(d => d.Id == Dep).Version.ToNormalizedString());
        Assert.Equal("Versioned 2.0 uses Dep 2.0 in 'Shared'", Describe(host, Versioned));

        var restarted = await LoadedHostAsync(SharedCell);
        Assert.Equal("Versioned 1.0 uses Dep 2.0 in 'Shared'", Describe(restarted, Versioned));
        Assert.StartsWith("UserB uses Dep 2.0 in 'Shared'", Describe(restarted, UserB), StringComparison.Ordinal);
        Assert.Empty((await restarted.ValidateAsync(Ct)).Issues);
    }

    [Fact]
    public async Task InstallingAPluginStagedForUninstall_CancelsTheUninstall_OrSwitchesItsVersion()
    {
        var host = await LoadedHostAsync();
        await host.InstallAsync(Versioned, V1, cancellationToken: Ct);
        Assert.Equal(ChangeStatus.Staged, (await host.UninstallAsync(Versioned, cancellationToken: Ct)).Status);

        var kept = await host.InstallAsync(Versioned, cancellationToken: Ct);

        Assert.Equal(InstallStatus.Installed, kept.Status);
        Assert.Contains("was staged for uninstall; the install cancelled that, and it stays at 1.0.0", kept.Reason);
        Assert.Equal(PluginStatus.Loaded, Assert.Single(kept.Plugins).Status);
        Assert.Null(new StateStore(_root.Path).Read().Find(Versioned)!.StagedChange);
        Assert.Empty(await host.GetPendingChangesAsync(Ct));

        Assert.Equal(ChangeStatus.Staged, (await host.UninstallAsync(Versioned, cancellationToken: Ct)).Status);
        var switched = await host.InstallAsync(Versioned, VersionRange.Parse("[2.0.0]"), cancellationToken: Ct);

        Assert.Equal(InstallStatus.PendingRestart, switched.Status);
        Assert.Contains("switches from 1.0.0 to 2.0.0 at the next startup", switched.Reason);
        AssertSingleChange(await host.GetPendingChangesAsync(Ct), PendingChangeKind.Update, "1.0.0", "2.0.0", isStaged: true);
        Assert.Equal("Versioned 2.0 uses Dep 2.0 in 'Fixture.Lifecycle.Versioned'", Describe(await LoadedHostAsync(), Versioned));
    }

    [Fact]
    public async Task InstallingAPluginUninstalledByCascade_AlsoKeepsThePluginsItNeeds()
    {
        var host = await LoadedHostAsync();
        await host.InstallAsync(User, cancellationToken: Ct);
        Assert.Equal(ChangeStatus.Staged, (await host.UninstallAsync(Versioned, cascade: true, Ct)).Status);

        var result = await host.InstallAsync(User, cancellationToken: Ct);

        Assert.Equal(InstallStatus.Installed, result.Status);
        Assert.Contains($"and so do its plugin dependencies {Versioned}", result.Reason);
        Assert.All(new StateStore(_root.Path).Read().Plugins, p => Assert.Null(p.StagedChange));
        var restarted = await LoadedHostAsync();
        Assert.Equal("User sees Versioned 1.0", Describe(restarted, User));
    }

    [Fact]
    public async Task StartupReportsTheStateToLoad_WhileHoldingTheRootLock_OrRightAwayWhenItsBusy()
    {
        var host = await LoadedHostAsync();
        await host.InstallAsync(Versioned, V1, cancellationToken: Ct);
        Assert.Equal(ChangeStatus.Staged, (await host.UpdateAsync(Versioned, VersionRange.Parse("[2.0.0]"), cancellationToken: Ct)).Status);
        var maintenance = new StartupMaintenance(new PackageLayout(_root.Path), 1, NullLogger.Instance);

        // Held by another process: no apply, but the record is written at once from the state as read.
        using (await RootLock.AcquireAsync(_root.Path, cancellationToken: Ct))
        {
            StateDocument? busy = null;
            await maintenance.ApplyStagedChangesAsync(TimeSpan.FromMilliseconds(200), s => busy = s, Ct);
            Assert.Equal("1.0.0", busy!.Find(Versioned)!.Version.ToNormalizedString());
        }

        // Free: the applied state is reported before the lock is released, so no cleanup can run in between.
        var lockedDuringCallback = false;
        StateDocument? applied = null;
        await maintenance.ApplyStagedChangesAsync(TimeSpan.FromSeconds(5), s =>
        {
            applied = s;
            using var probe = RootLock.TryAcquire(_root.Path);
            lockedDuringCallback = probe is null;
        }, Ct);

        Assert.True(lockedDuringCallback, "The state to load was reported after the root lock was released.");
        Assert.Equal("2.0.0", applied!.Find(Versioned)!.Version.ToNormalizedString());

        // LoadAsync uses it: the new host's record lists 2.0 (and its Dep) as soon as it has loaded.
        var restarted = await LoadedHostAsync();
        var record = File.ReadAllText(restarted.InUseRecordPath!);
        Assert.Contains("fixture.lifecycle.versioned/2.0.0", record);
        Assert.Contains("fixture.dep/2.0.0", record);
    }

    /// <summary>A process ID no process has (a child that has exited, so its ID is free again).</summary>
    private static int DeadProcessId()
    {
        using var child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("dotnet", "--version") { RedirectStandardOutput = true, UseShellExecute = false })!;
        child.WaitForExit();
        return child.Id;
    }

    private static void AssertSingleChange(IReadOnlyList<PendingChange> changes, PendingChangeKind kind, string? from, string? to, bool isStaged)
    {
        var change = Assert.Single(changes);
        Assert.Equal((kind, Versioned, from, to, isStaged), (change.Kind, change.PluginId, change.CurrentVersion?.ToNormalizedString(), change.NewVersion?.ToNormalizedString(), change.IsStaged));
    }

    private static StateDocument PackageRecorderMerge(StateDocument state, PluginState plugin) =>
        Alcove.Install.PackageRecorder.Merge(state, [plugin]);

    private async Task WriteStateAsync(StateDocument state)
    {
        using var rootLock = await RootLock.AcquireAsync(_root.Path, cancellationToken: Ct);
        new StateStore(_root.Path).Write(rootLock, state);
    }

    private string PackageFolder(string id, string version) => new PackageLayout(_root.Path).GetPackageDirectory(id, NuGetVersion.Parse(version));

    private async Task<PluginHost> LoadedHostAsync(Action<PluginHostOptions>? configure = null, HostEnvironment? environment = null)
    {
        var host = Create(configure, environment);
        await host.LoadAsync(Ct);
        return host;
    }

    private PluginHost Create(Action<PluginHostOptions>? configure = null, HostEnvironment? environment = null)
    {
        var options = new PluginHostOptions
        {
            Root = _root.Path,
            PluginPackageType = PackageType,
            HostOwnedPackages = { HostingFixtures.ContractsPackage },
            LoggerFactory = _logs,
        };
        configure?.Invoke(options);
        if (options.Feeds.Count == 0)
        {
            options.Feeds.Add(new PackageFeed(lifecycle.PackagesDirectory));
        }

        var host = environment is null ? PluginHost.Create(options) : PluginHost.Create(options, environment);
        _hosts.Add(host);
        return host;
    }

    private static string Describe(PluginHost host, string pluginId) =>
        host.Catalog.GetAll<IDescriber>().Single(e => e.Plugin.Id == pluginId).Value.Describe();

    /// <summary>Reports synchronously (<see cref="Progress{T}"/> posts to the thread pool).</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class RecordingVerifier : Alcove.Trust.IPackageVerifier
    {
        public List<string> Packages { get; } = [];

        public bool RemembersAcceptances => true;

        public Alcove.Trust.TrustDecision CheckFeed(string source) => new(true, false, []);

        public Alcove.Trust.TrustDecision CheckKeptSource(string packageId, NuGetVersion version, string? source) => new(true, false, []);

        public Task<Alcove.Trust.TrustDecision> VerifyPackageAsync(string nupkgPath, string packageId, NuGetVersion version, CancellationToken cancellationToken)
        {
            Assert.True(File.Exists(nupkgPath));
            lock (Packages)
            {
                Packages.Add(packageId);
            }

            return Task.FromResult(new Alcove.Trust.TrustDecision(true, false, []));
        }
    }

    private sealed class RejectingVerifier(string rejected) : Alcove.Trust.IPackageVerifier
    {
        public bool RemembersAcceptances => true;

        public Alcove.Trust.TrustDecision CheckFeed(string source) => new(true, false, []);

        public Alcove.Trust.TrustDecision CheckKeptSource(string packageId, NuGetVersion version, string? source) => new(true, false, []);

        public Task<Alcove.Trust.TrustDecision> VerifyPackageAsync(string nupkgPath, string packageId, NuGetVersion version, CancellationToken cancellationToken)
        {
            var reject = string.Equals(packageId, rejected, StringComparison.OrdinalIgnoreCase);
            return Task.FromResult(new Alcove.Trust.TrustDecision(!reject, false,
                reject ? [new Alcove.Trust.TrustIssue(Alcove.Trust.TrustIssueSeverity.Error, "TEST001", "not trusted")] : []));
        }
    }
}

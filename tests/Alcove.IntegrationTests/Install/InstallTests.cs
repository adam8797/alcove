using System.Net;
using Alcove.Cells;
using Alcove.Feeds;
using Alcove.Hosting;
using Alcove.Install;
using Alcove.IntegrationTests.Infrastructure;
using Alcove.Storage;
using Alcove.Trust;
using Fixture.HostContracts;
using NuGet.Versioning;

namespace Alcove.IntegrationTests.Install;

/// <summary>
/// <see cref="PluginHost.InstallAsync"/> end to end with real plugin packages (tests/fixtures/Hosting) from a local
/// folder feed and a localhost HTTP feed: the implementation plan's Phase 4 "Done when" scenarios.
/// </summary>
public sealed class InstallTests(HostingFixtures fixtures) : IDisposable
{
    private const string UserA = "Fixture.Hosting.UserA";
    private const string UserB = "Fixture.Hosting.UserB";
    private const string Provider = "Fixture.Hosting.Provider";
    private const string Consumer = "Fixture.Hosting.Consumer";
    private const string Dep = "Fixture.Dep";
    private const string PackageType = "FixturePlugin";

    private readonly TempDirectory _root = new("alcove-install");
    private readonly TempDirectory _host = new("alcove-host");
    private readonly ListLoggerFactory _logs = new();
    private readonly List<PluginHost> _hosts = [];
    private readonly List<IDisposable> _disposables = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (var host in _hosts)
        {
            host.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }

        _root.DisposeBestEffort();   // cells keep the root's assemblies loaded
        _host.Dispose();
    }

    [Fact]
    public async Task InstallingFromAFolderFeedWhileRunning_LoadsThePluginIntoTheCatalogWithoutARestart()
    {
        var host = await LoadedHostAsync();
        var added = new List<string>();
        host.Catalog.ExtensionsAdded += (_, e) => added.Add(e.Plugin.Id);
        var steps = new List<InstallProgress>();

        var result = await host.InstallAsync(UserA, progress: new SyncProgress<InstallProgress>(steps.Add), cancellationToken: Ct);

        Assert.True(result.Status == InstallStatus.Loaded, result.ToString());
        Assert.Equal(NuGetVersion.Parse("1.0.0"), result.Version);
        Assert.Equal([UserA], result.Plugins.Select(p => p.Id));
        Assert.Equal([UserA], added);
        Assert.Equal("UserA uses Dep 1.0 in 'Fixture.Hosting.UserA'", Describe(host, UserA));
        Assert.Equal(PluginStatus.Loaded, Assert.Single(host.Plugins).Status);
        Assert.Equal(
            [InstallStep.Resolving, InstallStep.Downloading, InstallStep.VerifyingSignatures, InstallStep.Downloading, InstallStep.VerifyingSignatures,
             InstallStep.Extracting, InstallStep.Extracting, InstallStep.Recording, InstallStep.Loading],
            steps.Select(s => s.Step));

        // Recorded like any install: lowercase layout, the .nupkg kept, contentHash pinned, the dependency set.
        var state = Assert.Single(new StateStore(_root.Path).Read().Plugins);
        Assert.Equal(UserA, state.Id);
        var dep = state.Dependencies.Single(d => d.Id == Dep);
        Assert.Equal((NuGetVersion.Parse("1.0.0"), DependencyClass.CellPrivate), (dep.Version, dep.Classification));
        Assert.NotNull(dep.ContentHash);
        Assert.Equal(DependencyClass.HostOwned, state.Dependencies.Single(d => d.Id == HostingFixtures.ContractsPackage).Classification);
        Assert.True(File.Exists(Path.Combine(_root.Path, "packages", "fixture.hosting.usera", "1.0.0", "fixture.hosting.usera.1.0.0.nupkg")));
        Assert.True(File.Exists(Path.Combine(_root.Path, "packages", "fixture.dep", "1.0.0", ".nupkg.metadata")));

        // The next startup loads it from the state file, offline, without issues.
        var restarted = Create();
        Assert.Empty((await restarted.ValidateAsync(Ct)).Issues);
        await restarted.LoadAsync(Ct);
        Assert.Equal("UserA uses Dep 1.0 in 'Fixture.Hosting.UserA'", Describe(restarted, UserA));
    }

    [Fact]
    public async Task APluginDependency_IsInstalledAsAPluginAndLoadedFirst()
    {
        var host = await LoadedHostAsync();
        var added = new List<string>();
        host.Catalog.ExtensionsAdded += (_, e) => added.Add(e.Plugin.Id);

        var result = await host.InstallAsync(Consumer, cancellationToken: Ct);

        Assert.True(result.Status == InstallStatus.Loaded, result.ToString());
        Assert.Equal(Consumer, result.PackageId);
        Assert.Equal([Provider, Consumer], result.Plugins.Select(p => p.Id));
        Assert.All(result.Plugins, p => Assert.Equal(PluginStatus.Loaded, p.Status));
        Assert.Equal([Provider, Consumer], added);
        Assert.Equal(
            "Consumer: IS my Widget: Widget(Dep 2.0 in 'Fixture.Hosting.Provider'); ProviderService in 'Fixture.Hosting.Provider'",
            Describe(host, Consumer));

        var state = new StateStore(_root.Path).Read();
        Assert.Equal([Provider, Consumer], state.Plugins.Select(p => p.Id));
        Assert.Equal(["Fixture.Dep"], state.Find(Provider)!.ExposedDependencies);
        Assert.Equal(
            [(Dep, "2.0.0", DependencyClass.CellPrivate), (HostingFixtures.ContractsPackage, TestEnvironment.AlcoveVersion, DependencyClass.HostOwned), (Provider, "1.0.0", DependencyClass.Plugin)],
            state.Find(Consumer)!.Dependencies.Select(d => (d.Id, d.Version.ToNormalizedString(), d.Classification)));

        // Installing the dependency again changes nothing.
        var again = await host.InstallAsync(Provider, cancellationToken: Ct);
        Assert.Equal(InstallStatus.Loaded, again.Status);
        Assert.Equal(2, host.Plugins.Count);
    }

    [Fact]
    public async Task InstallingFromALocalHttpFeed_LoadsThePlugin_AndSearchFiltersByPackageType()
    {
        using var feed = new LocalNuGetFeed(fixtures.PackagesDirectory);
        var host = await LoadedHostAsync(o => o.Feeds.Add(new PackageFeed(feed.ServiceIndex)));

        var results = await host.SearchAsync("", Ct);
        Assert.Contains(results, r => r.Id == UserB && r.IsCompatible && r.Source == feed.ServiceIndex);
        Assert.DoesNotContain(results, r => r.Id == Dep);
        Assert.Contains(feed.Requests, r => r.Contains("/query", StringComparison.Ordinal));

        var result = await host.InstallAsync(UserB, cancellationToken: Ct);

        Assert.True(result.Status == InstallStatus.Loaded, result.ToString());
        Assert.Equal("UserB uses Dep 2.0 in 'Fixture.Hosting.UserB' and NewIn2 present; services: False", Describe(host, UserB));
        Assert.Contains(feed.Requests, r => r.StartsWith("200 GET /flat/fixture.hosting.userb/1.0.0/", StringComparison.Ordinal));
        Assert.Equal([NuGetVersion.Parse("1.0.0"), NuGetVersion.Parse("2.0.0"), NuGetVersion.Parse("3.0.0")], await host.GetVersionsAsync(Dep, Ct));
    }

    [Fact]
    public async Task AnAuthenticatedHttpFeed_UsesTheAppsCredentialProvider()
    {
        var credentials = new NetworkCredential("alcove", "s3cret");
        using var feed = new LocalNuGetFeed(fixtures.PackagesDirectory, credentials);
        var asked = new List<(Uri Feed, bool IsRetry)>();
        var host = await LoadedHostAsync(o =>
        {
            o.Feeds.Add(new PackageFeed(feed.ServiceIndex));
            o.CredentialProvider = new CredentialProvider((uri, retry) =>
            {
                asked.Add((uri, retry));
                return credentials;
            });
        });

        var result = await host.InstallAsync(UserA, cancellationToken: Ct);

        Assert.True(result.Status == InstallStatus.Loaded, result.ToString());
        Assert.Equal((new Uri(feed.ServiceIndex), false), Assert.Single(asked));
        Assert.Contains(feed.Requests, r => r.StartsWith("401 ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnAuthenticatedHttpFeed_AcceptsTheFeedsOwnCredentials_AndFailsClearlyWithout()
    {
        var credentials = new NetworkCredential("alcove", "s3cret");
        using var feed = new LocalNuGetFeed(fixtures.PackagesDirectory, credentials);

        var anonymous = await LoadedHostAsync(o => o.Feeds.Add(new PackageFeed(feed.ServiceIndex)));
        var failed = await anonymous.InstallAsync(UserA, cancellationToken: Ct);
        Assert.Equal(InstallStatus.Failed, failed.Status);
        Assert.Contains(feed.ServiceIndex, failed.Reason);
        Assert.Contains("401", failed.Reason);
        Assert.False(failed.IsInstalled);
        Assert.Empty(new StateStore(_root.Path).Read().Plugins);

        var withCredentials = Create(o => o.Feeds.Add(new PackageFeed(feed.ServiceIndex) { Credentials = credentials }));
        var result = await withCredentials.InstallAsync(UserA, cancellationToken: Ct);
        Assert.Equal(InstallStatus.Installed, result.Status);   // that host hasn't loaded yet
    }

    [Fact]
    public async Task FeedsAndCredentialsCanComeFromANuGetConfig()
    {
        var credentials = new NetworkCredential("alcove", "s3cret");
        using var feed = new LocalNuGetFeed(fixtures.PackagesDirectory, credentials);
        var config = _host.Combine("nuget.config");
        File.WriteAllText(config, $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="private" value="{feed.ServiceIndex}" />
                <add key="disabled" value="{_host.Combine("nowhere")}" />
              </packageSources>
              <disabledPackageSources>
                <add key="disabled" value="true" />
              </disabledPackageSources>
              <packageSourceCredentials>
                <private>
                  <add key="Username" value="alcove" />
                  <add key="ClearTextPassword" value="s3cret" />
                </private>
              </packageSourceCredentials>
            </configuration>
            """);
        var host = await LoadedHostAsync(o =>
        {
            o.Feeds.Clear();
            o.NuGetConfigPath = config;
        });

        var result = await host.InstallAsync(UserA, cancellationToken: Ct);

        Assert.True(result.Status == InstallStatus.Loaded, result.ToString());
        Assert.Contains(feed.Requests, r => r.StartsWith("200 GET /flat/fixture.hosting.usera/1.0.0/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnInstallThatWouldChangeAProvidersExposedDependency_IsPendingRestart()
    {
        // Provider exposes Fixture.Dep 2.0 from cell 'Shared'. Consumer3 needs Dep >= 3.0 in the same cell, so the
        // joint resolution moves the cell to Dep 3.0, which would change what Provider's consumers get.
        void SharedCell(PluginHostOptions o) => o.Cells.Add(new CellDefinition("Shared") { Plugins = { Provider, "Fixture.Hosting.Consumer3" } });
        var host = await LoadedHostAsync(SharedCell);
        Assert.Equal(InstallStatus.Loaded, (await host.InstallAsync(Provider, cancellationToken: Ct)).Status);

        var result = await host.InstallAsync("Fixture.Hosting.Consumer3", cancellationToken: Ct);

        Assert.Equal(InstallStatus.PendingRestart, result.Status);
        Assert.Equal([Dep], result.ConflictingNames);
        Assert.Contains($"It would change {Dep}, an exposed dependency of {Provider} in cell 'Shared'", result.Reason);
        Assert.Contains("private Fixture.Dep 2.0.0", result.Reason);
        Assert.Contains("private Fixture.Dep 3.0.0", result.Reason);
    }

    [Fact]
    public async Task APluginNeedingAPrivateVersionOfANameItsCellTookFromTheHost_IsPendingRestart()
    {
        // The host ships Fixture.Dep 1.0.0 (host-shared). UserA (Dep >= 1.0) takes it from the host; UserB (Dep >= 2.0)
        // joins the same cell, which would need a private Dep 2.0 instead: the cell already committed to the host's.
        var environment = FabricatedHost.With(_host, (Dep, "1.0.0", "1.0.0.0"));
        void SharedCell(PluginHostOptions o) => o.Cells.Add(new CellDefinition("Shared") { Plugins = { UserA, UserB } });
        var host = await LoadedHostAsync(SharedCell, environment);

        var first = await host.InstallAsync(UserA, cancellationToken: Ct);
        Assert.True(first.Status == InstallStatus.Loaded, first.ToString());
        Assert.Equal(DependencyClass.HostShared, new StateStore(_root.Path).Read().Find(UserA)!.Dependencies.Single(d => d.Id == Dep).Classification);

        var second = await host.InstallAsync(UserB, cancellationToken: Ct);

        Assert.Equal(InstallStatus.PendingRestart, second.Status);
        Assert.True(second.IsInstalled);
        Assert.Equal([Dep], second.ConflictingNames);
        Assert.Contains("Cell 'Shared'", second.Reason);
        Assert.Contains(Dep, second.Reason);
        var userB = Assert.Single(second.Plugins);
        Assert.Equal(PluginStatus.NotLoaded, userB.Status);
        Assert.Contains("next startup", userB.Reason);
        Assert.Contains(host.Plugins, p => p.Id == UserB && p.Status == PluginStatus.NotLoaded);
        Assert.DoesNotContain(host.Catalog.GetAll<IDescriber>(), e => e.Plugin.Id == UserB);

        // The cell was resolved again: both now use a private Dep 2.0, from the next startup on.
        var state = new StateStore(_root.Path).Read();
        Assert.All([state.Find(UserA)!, state.Find(UserB)!], p =>
            Assert.Equal(("2.0.0", DependencyClass.CellPrivate), (p.Dependencies.Single(d => d.Id == Dep).Version.ToNormalizedString(), p.Dependencies.Single(d => d.Id == Dep).Classification)));

        var restarted = Create(SharedCell, environment);
        await restarted.LoadAsync(Ct);
        Assert.Equal("UserA uses Dep 2.0 in 'Shared'", Describe(restarted, UserA));
        Assert.StartsWith("UserB uses Dep 2.0 in 'Shared'", Describe(restarted, UserB), StringComparison.Ordinal);
    }

    [Fact]
    public async Task APluginNeedingMoreOfAHostOwnedPackageThanTheHostHas_IsIncompatible_AndSearchFlagsIt()
    {
        var environment = FabricatedHost.With(_host, (Dep, "1.0.0", "1.0.0.0"));
        var host = await LoadedHostAsync(o => o.HostOwnedPackages.Add(Dep), environment);

        var search = await host.SearchAsync("Fixture.Hosting.User", Ct);
        var userB = search.Single(r => r.Id == UserB);
        Assert.False(userB.IsCompatible);
        Assert.Contains("Fixture.Dep (>= 2.0.0)", userB.IncompatibilityReason);
        Assert.Contains("this host has 1.0.0", userB.IncompatibilityReason);
        Assert.True(search.Single(r => r.Id == UserA).IsCompatible);

        var result = await host.InstallAsync(UserB, cancellationToken: Ct);

        Assert.Equal(InstallStatus.Incompatible, result.Status);
        Assert.Contains("Fixture.Dep (>= 2.0.0)", result.Reason);
        Assert.Contains("the host has 1.0.0", result.Reason);
        Assert.Empty(result.Plugins);
        Assert.Empty(new StateStore(_root.Path).Read().Plugins);
        Assert.Empty(host.Plugins);
    }

    [Fact]
    public async Task InstallingBeforeLoad_OnlyRecordsThePlugin_AndLoadAsyncLoadsIt()
    {
        var host = Create();

        var result = await host.InstallAsync(UserA, cancellationToken: Ct);

        Assert.Equal(InstallStatus.Installed, result.Status);
        Assert.Equal(PluginStatus.NotLoaded, Assert.Single(result.Plugins).Status);
        Assert.Empty(host.Plugins);
        Assert.Empty(host.Catalog.GetAll<IDescriber>());

        await host.LoadAsync(Ct);
        Assert.Equal("UserA uses Dep 1.0 in 'Fixture.Hosting.UserA'", Describe(host, UserA));
    }

    [Fact]
    public async Task TwoInstallsInParallel_AreSerialised_AndBothAreRecorded()
    {
        var host = await LoadedHostAsync();
        var other = Create();   // another host on the same root, like another instance of the app

        var results = await Task.WhenAll(
            host.InstallAsync(UserA, cancellationToken: Ct),
            other.InstallAsync(UserB, cancellationToken: Ct),
            host.InstallAsync(Consumer, cancellationToken: Ct));

        Assert.Equal([InstallStatus.Loaded, InstallStatus.Installed, InstallStatus.Loaded], results.Select(r => r.Status));
        Assert.Equal([Consumer, Provider, UserA, UserB], new StateStore(_root.Path).Read().Plugins.Select(p => p.Id).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task APluginWhoseDependencyAnotherInstanceInstalled_IsPendingRestart()
    {
        var host = await LoadedHostAsync();
        Assert.Equal(InstallStatus.Installed, (await Create().InstallAsync(Provider, cancellationToken: Ct)).Status);

        var result = await host.InstallAsync(Consumer, cancellationToken: Ct);

        Assert.Equal(InstallStatus.PendingRestart, result.Status);
        Assert.Equal([Consumer], result.Plugins.Select(p => p.Id));   // Provider was already installed
        Assert.Contains($"depends on the plugin {Provider}, which isn't loaded in this process", result.Reason);
        Assert.Empty(result.ConflictingNames);
    }

    [Fact]
    public async Task AnInstall_WaitsForTheRootLock()
    {
        var host = await LoadedHostAsync();
        var rootLock = await RootLock.AcquireAsync(_root.Path, cancellationToken: Ct);

        var install = host.InstallAsync(UserA, cancellationToken: Ct);
        await Task.Delay(TimeSpan.FromMilliseconds(500), Ct);
        Assert.False(install.IsCompleted, "The install went ahead while another process held the root lock.");
        Assert.Empty(new StateStore(_root.Path).Read().Plugins);

        rootLock.Dispose();
        Assert.Equal(InstallStatus.Loaded, (await install).Status);
    }

    [Fact]
    public async Task UnknownPackagesAndLibraries_AreClearFailures()
    {
        var host = await LoadedHostAsync();

        var missing = await host.InstallAsync("Fixture.Nope", cancellationToken: Ct);
        Assert.Equal(InstallStatus.Failed, missing.Status);
        Assert.Contains("wasn't found", missing.Reason);

        var library = await host.InstallAsync(Dep, cancellationToken: Ct);
        Assert.Equal(InstallStatus.Failed, library.Status);
        Assert.Contains("isn't a plugin package", library.Reason);

        var range = await host.InstallAsync(UserA, VersionRange.Parse("[2.0.0, )"), cancellationToken: Ct);
        Assert.Equal(InstallStatus.Failed, range.Status);
        Assert.Contains("No stable version of Fixture.Hosting.UserA (>= 2.0.0)", range.Reason);

        Assert.Empty(new StateStore(_root.Path).Read().Plugins);
    }

    [Fact]
    public async Task TheTrustPolicyHooks_RunBeforeDownloadAndBeforeExtraction()
    {
        var host = await LoadedHostAsync();
        var verifier = new RecordingVerifier { Reject = Dep };
        host.Verifier = verifier;

        var result = await host.InstallAsync(UserA, cancellationToken: Ct);

        Assert.Equal(InstallStatus.Failed, result.Status);
        Assert.Contains("The trust policy rejected Fixture.Dep 1.0.0: [TEST001] not trusted", result.Reason);
        Assert.Contains(result.Issues, i => i.Code == "TEST001" && i.PluginId == Dep);
        Assert.Equal([fixtures.PackagesDirectory], verifier.Feeds);
        Assert.Contains(Dep, verifier.Packages);
        Assert.False(Directory.Exists(Path.Combine(_root.Path, "packages", "fixture.dep")), "A rejected package was extracted.");
        Assert.Empty(new StateStore(_root.Path).Read().Plugins);

        host.Verifier = new RecordingVerifier { RejectFeeds = true };
        var noFeed = await host.InstallAsync(UserA, cancellationToken: Ct);
        Assert.Equal(InstallStatus.Failed, noFeed.Status);
        Assert.Contains("the trust policy doesn't allow " + fixtures.PackagesDirectory, noFeed.Reason);
    }

    [Fact]
    public async Task APackageAlreadyInThePluginRoot_IsVerifiedAgainstTheCurrentPolicy_WhenANewPlanUsesIt()
    {
        // Review finding 4: Provider's install extracted Fixture.Dep 2.0. UserB resolves to that copy instead of
        // downloading it; the policy now rejects it, so the install must fail.
        var first = await LoadedHostAsync();
        Assert.Equal(InstallStatus.Loaded, (await first.InstallAsync(Provider, Ct)).Status);
        var host = await LoadedHostAsync();
        var verifier = new RecordingVerifier { Reject = Dep };
        host.Verifier = verifier;

        var result = await host.InstallAsync(UserB, Ct);

        Assert.Equal(InstallStatus.Failed, result.Status);
        Assert.Contains("The trust policy rejected Fixture.Dep 2.0.0 (already in the plugin root): [TEST001] not trusted", result.Reason);
        Assert.Contains(result.Issues, i => i.Code == "TEST001" && i.PluginId == Dep);
        Assert.Equal([UserB, Dep], verifier.Packages);   // UserB downloaded; Dep from its kept .nupkg
        Assert.Null(new StateStore(_root.Path).Read().Find(UserB));
        Assert.False(Directory.Exists(Path.Combine(_root.Path, "packages", "fixture.hosting.userb")), "A package was extracted although the plan was rejected.");
    }

    [Theory]
    [InlineData("../Fixture.Hosting.UserA")]
    [InlineData("Fixture.Hosting.UserA/..")]
    public async Task AnInvalidPackageId_FailsWithoutTouchingTheFeedsOrTheRoot(string packageId)
    {
        // Review finding 10: package IDs become paths.
        var host = await LoadedHostAsync();
        var verifier = new RecordingVerifier();
        host.Verifier = verifier;

        var result = await host.InstallAsync(packageId, Ct);

        Assert.Equal(InstallStatus.Failed, result.Status);
        Assert.Contains("isn't a valid package ID", result.Reason);
        Assert.Empty(verifier.Feeds);
        Assert.Empty(await host.GetVersionsAsync(packageId, Ct));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task APolicysVerdictOnAPackage_IsRemembered_AndTheSourceIsCheckedOnEveryReuse(bool remembers)
    {
        // Round 2, finding 4; round 3, finding 3: remembered for the host's lifetime offline, for 10 minutes online.
        var host = await LoadedHostAsync();
        var verifier = new RecordingVerifier { RemembersAcceptances = remembers };
        host.Verifier = verifier;

        Assert.Equal(InstallStatus.Loaded, (await host.InstallAsync(Provider, Ct)).Status);
        Assert.Equal(InstallStatus.Loaded, (await host.InstallAsync(UserB, Ct)).Status);

        // Dep 2.0 was verified when downloaded for Provider; UserB's plan reuses it without verifying it again (online
        // acceptances are remembered briefly; TrustVerdictsTests covers the expiry).
        Assert.Equal([Dep, Provider, UserB], verifier.Packages);

        // Its source feed is recorded, and checked on reuse (always).
        var state = new StateStore(_root.Path).Read();
        Assert.Equal(fixtures.PackagesDirectory, state.Find(Provider)!.Source);
        Assert.Equal(fixtures.PackagesDirectory, state.Find(Provider)!.Dependencies.Single(d => d.Id == Dep).Source);
        Assert.Equal([(Dep, (string?)fixtures.PackagesDirectory)], verifier.KeptSources);
    }

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
        if (configure is null || !Configures(configure))
        {
            options.Feeds.Add(new PackageFeed(fixtures.PackagesDirectory));
        }

        configure?.Invoke(options);
        var host = environment is null ? PluginHost.Create(options) : PluginHost.Create(options, environment);
        _hosts.Add(host);
        return host;
    }

    /// <summary>Whether <paramref name="configure"/> sets its own feeds (then the folder feed isn't added).</summary>
    private static bool Configures(Action<PluginHostOptions> configure)
    {
        var probe = new PluginHostOptions();
        configure(probe);
        return probe.Feeds.Count > 0;
    }

    private static string Describe(PluginHost host, string pluginId) =>
        host.Catalog.GetAll<IDescriber>().Single(e => e.Plugin.Id == pluginId).Value.Describe();

    /// <summary>Reports synchronously (<see cref="Progress{T}"/> posts to the thread pool).</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class CredentialProvider(Func<Uri, bool, NetworkCredential?> get) : ICredentialProvider
    {
        public Task<NetworkCredential?> GetCredentialsAsync(Uri feed, bool isRetry, CancellationToken cancellationToken) =>
            Task.FromResult(get(feed, isRetry));
    }

    private sealed class RecordingVerifier : IPackageVerifier
    {
        public string? Reject { get; init; }

        public bool RejectFeeds { get; init; }

        public bool RemembersAcceptances { get; init; } = true;

        public List<string> Feeds { get; } = [];

        public List<(string Id, string? Source)> KeptSources { get; } = [];

        public List<string> Packages { get; } = [];

        public TrustDecision CheckFeed(string source)
        {
            Feeds.Add(source);
            return new TrustDecision(!RejectFeeds, false, RejectFeeds ? [new TrustIssue(TrustIssueSeverity.Error, "TEST002", "not an allowed feed")] : []);
        }

        public TrustDecision CheckKeptSource(string packageId, NuGetVersion version, string? source)
        {
            KeptSources.Add((packageId, source));
            return new TrustDecision(true, false, []);
        }

        public Task<TrustDecision> VerifyPackageAsync(string nupkgPath, string packageId, NuGetVersion version, CancellationToken cancellationToken)
        {
            Assert.True(File.Exists(nupkgPath));
            Packages.Add(packageId);
            var rejected = string.Equals(packageId, Reject, StringComparison.OrdinalIgnoreCase);
            return Task.FromResult(new TrustDecision(!rejected, false, rejected ? [new TrustIssue(TrustIssueSeverity.Error, "TEST001", "not trusted")] : []));
        }
    }
}

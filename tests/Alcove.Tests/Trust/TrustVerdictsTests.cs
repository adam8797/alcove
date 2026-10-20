using Alcove.Install;
using Alcove.Storage;
using Alcove.Tests.TestSupport;
using Alcove.Trust;
using NuGet.Versioning;

namespace Alcove.Tests.Trust;

/// <summary>Packages already in the plugin root that a new plan reuses (code review round 2, finding 4).</summary>
public sealed class TrustVerdictsTests : IDisposable
{
    private static readonly NuGetVersion V1 = NuGetVersion.Parse("1.0.0");
    private readonly TempDirectory _root = new();
    private readonly PackageLayout _layout;

    public TrustVerdictsTests()
    {
        _layout = new PackageLayout(_root.Path);
        var nupkg = _layout.GetPackageFilePath("A", V1);
        Directory.CreateDirectory(Path.GetDirectoryName(nupkg)!);
        File.WriteAllText(nupkg, "kept");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _root.Dispose();

    [Fact]
    public async Task WithOnlineRevocationChecks_AnAcceptanceIsRememberedForTenMinutes()
    {
        // Round 3, finding 3: online acceptances are cached briefly, so a revocation is seen soon.
        var clock = new ManualClock();
        var verifier = new CountingVerifier { RemembersAcceptances = false };
        var verdicts = new TrustVerdicts(clock);

        await verdicts.VerifyDownloadAsync(verifier, _layout.GetPackageFilePath("A", V1), "A", V1, Ct);
        clock.Advance(TrustVerdicts.OnlineAcceptanceLifetime - TimeSpan.FromSeconds(1));
        Assert.Null(await verdicts.VerifyKeptAsync(verifier, _layout, "A", V1, "/srv/feed", Ct));
        Assert.Equal(1, verifier.Verified);

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True((await verdicts.VerifyKeptAsync(verifier, _layout, "A", V1, "/srv/feed", Ct))!.Allowed);
        Assert.Equal(2, verifier.Verified);
        Assert.Null(await verdicts.VerifyKeptAsync(verifier, _layout, "A", V1, "/srv/feed", Ct));   // remembered again from then on
        Assert.Equal(2, verifier.Verified);
    }

    [Fact]
    public async Task WithOfflineRevocationChecks_AnAcceptanceIsRememberedForTheHostsLifetime()
    {
        var clock = new ManualClock();
        var verifier = new CountingVerifier { RemembersAcceptances = true };
        var verdicts = new TrustVerdicts(clock);

        Assert.True((await verdicts.VerifyKeptAsync(verifier, _layout, "A", V1, "/srv/feed", Ct))!.Allowed);
        clock.Advance(TimeSpan.FromDays(30));
        Assert.Null(await verdicts.VerifyKeptAsync(verifier, _layout, "A", V1, "/srv/feed", Ct));

        Assert.Equal(1, verifier.Verified);
    }

    [Fact]
    public async Task WithRememberedAcceptances_AReusedPackageIsVerifiedOnce()
    {
        var verifier = new CountingVerifier { RemembersAcceptances = true };
        var verdicts = new TrustVerdicts();

        Assert.True((await verdicts.VerifyKeptAsync(verifier, _layout, "A", V1, "/srv/feed", Ct))!.Allowed);
        Assert.Null(await verdicts.VerifyKeptAsync(verifier, _layout, "A", V1, "/srv/feed", Ct));

        Assert.Equal(1, verifier.Verified);
    }

    [Fact]
    public async Task TheRecordedSource_IsCheckedEvenWhenTheAcceptanceIsRemembered()
    {
        var verifier = new CountingVerifier { RemembersAcceptances = true };
        var verdicts = new TrustVerdicts();
        Assert.True((await verdicts.VerifyKeptAsync(verifier, _layout, "A", V1, "/srv/feed", Ct))!.Allowed);

        var decision = await verdicts.VerifyKeptAsync(verifier, _layout, "A", V1, source: null, Ct);

        Assert.False(decision!.Allowed);
        Assert.Equal(TrustIssueCodes.UnknownSource, Assert.Single(decision.Issues).Code);
        Assert.Equal(1, verifier.Verified);
    }

    [Fact]
    public void RecordedSource_PrefersTheStateFile_ThenThePackagesMetadata()
    {
        var dependency = new ResolvedDependency { Id = "Dep", Version = V1, Classification = DependencyClass.CellPrivate, ContentHash = "x", Source = "/srv/dep-feed" };
        var state = new StateDocument
        {
            Plugins =
            [
                new PluginState
                {
                    Id = "A",
                    Version = NuGetVersion.Parse("2.0.0"),
                    ContentHash = "x",
                    Source = "https://new.example.com/v3/index.json",
                    PreviousVersions = [new PluginState { Id = "A", Version = V1, ContentHash = "y", Source = "/srv/old-feed" }],
                    Dependencies = [dependency],
                },
            ],
        };
        WriteMetadata("A", NuGetVersion.Parse("2.0.0"), "/srv/metadata-feed");
        WriteMetadata("Orphan", V1, "/srv/orphan-feed");
        WriteMetadata("NoSource", V1, source: null);

        Assert.Equal("https://new.example.com/v3/index.json", PluginInstaller.RecordedSource(state, _layout, "a", NuGetVersion.Parse("2.0.0")));
        Assert.Equal("/srv/old-feed", PluginInstaller.RecordedSource(state, _layout, "A", V1));
        Assert.Equal("/srv/dep-feed", PluginInstaller.RecordedSource(state, _layout, "Dep", V1));
        Assert.Equal("/srv/orphan-feed", PluginInstaller.RecordedSource(state, _layout, "Orphan", V1));   // state files without sources, orphans
        Assert.Null(PluginInstaller.RecordedSource(state, _layout, "NoSource", V1));
        Assert.Null(PluginInstaller.RecordedSource(state, _layout, "Missing", V1));
    }

    private void WriteMetadata(string id, NuGetVersion version, string? source)
    {
        var path = _layout.GetNupkgMetadataPath(id, version);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, source is null
            ? """{ "version": 2, "contentHash": "x" }"""
            : $$"""{ "version": 2, "contentHash": "x", "source": "{{source}}" }""");
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class CountingVerifier : IPackageVerifier
    {
        public bool RemembersAcceptances { get; init; }

        public int Verified { get; private set; }

        public TrustDecision CheckFeed(string source) => new(true, false, []);

        public TrustDecision CheckKeptSource(string packageId, NuGetVersion version, string? source) => source is null
            ? new TrustDecision(false, false, [new TrustIssue(TrustIssueSeverity.Error, TrustIssueCodes.UnknownSource, "unknown")])
            : new TrustDecision(true, false, []);

        public Task<TrustDecision> VerifyPackageAsync(string nupkgPath, string packageId, NuGetVersion version, CancellationToken cancellationToken)
        {
            Verified++;
            return Task.FromResult(new TrustDecision(true, false, []));
        }
    }
}

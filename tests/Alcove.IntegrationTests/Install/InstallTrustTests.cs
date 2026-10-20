using System.Security.Cryptography;
using Alcove.Feeds;
using Alcove.Install;
using Alcove.IntegrationTests.Infrastructure;
using Alcove.IntegrationTests.Trust;
using Alcove.Packaging;
using Alcove.Storage;
using Alcove.Trust;
using Alcove.Validation;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Packaging.Signing;
using NuGet.Versioning;
using HashAlgorithmName = NuGet.Common.HashAlgorithmName;

namespace Alcove.IntegrationTests.Install;

/// <summary>
/// <see cref="PluginHost.InstallAsync"/> with the real trust policy (<see cref="PackageVerifier"/>): one small plugin
/// package, unsigned, author-signed with an in-memory self-signed certificate, and tampered, each in its own folder
/// feed. No network, no certificate store.
/// </summary>
public sealed class InstallTrustTests : IAsyncLifetime
{
    private const string Plugin = "Trust.Plugin";
    private const string PackageType = "FixturePlugin";

    private readonly TempDirectory _feeds = new("alcove-trust-feeds");
    private readonly TempDirectory _root = new("alcove-root");
    private readonly List<PluginHost> _hosts = [];
    private string _fingerprint = "";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string UnsignedFeed => _feeds.Combine("unsigned");

    private string SignedFeed => _feeds.Combine("signed");

    private string TamperedFeed => _feeds.Combine("tampered");

    public async ValueTask InitializeAsync()
    {
        foreach (var feed in new[] { UnsignedFeed, SignedFeed, TamperedFeed })
        {
            Directory.CreateDirectory(feed);
        }

        var unsigned = Path.Combine(UnsignedFeed, $"{Plugin}.1.0.0.nupkg");
        CreatePlugin(unsigned);
        var signed = Path.Combine(SignedFeed, $"{Plugin}.1.0.0.nupkg");
        using (var certificate = TrustTestPackages.CreateCodeSigningCertificate("Alcove Install Test Author"))
        {
            _fingerprint = certificate.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256);
            using var request = new AuthorSignPackageRequest(certificate, HashAlgorithmName.SHA256);
            using var options = SigningOptions.CreateFromFilePaths(
                unsigned, signed, overwrite: true, new X509SignatureProvider(timestampProvider: null), NuGet.Common.NullLogger.Instance);
            await SigningUtility.SignAsync(options, request, Ct);
        }

        var tampered = Path.Combine(TamperedFeed, $"{Plugin}.1.0.0.nupkg");
        File.Copy(signed, tampered);
        TrustTestPackages.FlipByteInEntry(tampered, "content/payload.bin");
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts)
        {
            await host.DisposeAsync();
        }

        _feeds.Dispose();
        _root.DisposeBestEffort();
    }

    [Fact]
    public async Task TheDefaultPolicy_InstallsAnUnsignedPlugin()
    {
        var host = await LoadedHostAsync(UnsignedFeed, new TrustPolicy());

        var result = await host.InstallAsync(Plugin, cancellationToken: Ct);

        Assert.True(result.Status == InstallStatus.Loaded, result.ToString());
        Assert.Equal(Plugin, Assert.Single(new StateStore(_root.Path).Read().Plugins).Id);
    }

    [Fact]
    public async Task RequiringSignedPackages_RejectsAnUnsignedPlugin_AndWritesNothing()
    {
        var host = await LoadedHostAsync(UnsignedFeed, new TrustPolicy { RequireSignedPackages = true });

        var result = await host.InstallAsync(Plugin, cancellationToken: Ct);

        Assert.Equal(InstallStatus.Failed, result.Status);
        Assert.Contains($"The trust policy rejected {Plugin} 1.0.0", result.Reason);
        Assert.Contains(result.Issues, i => i.Code == TrustIssueCodes.NotSigned && i.Severity == ValidationSeverity.Error && i.PluginId == Plugin);
        AssertNothingInstalled();
    }

    [Fact]
    public async Task AnAllowListedSelfSignedAuthor_IsInstalledAndLoaded()
    {
        var policy = new TrustPolicy
        {
            RequireSignedPackages = true,
            AllowedSigners = { new AllowedSigner { Kind = SignerKind.Author, Fingerprint = _fingerprint, AllowUntrustedRoot = true, Name = "Install test author" } },
        };
        var host = await LoadedHostAsync(SignedFeed, policy);

        var result = await host.InstallAsync(Plugin, cancellationToken: Ct);

        Assert.True(result.Status == InstallStatus.Loaded, result.ToString());
        Assert.DoesNotContain(result.Issues, i => i.Severity == ValidationSeverity.Error);
        Assert.True(File.Exists(Path.Combine(_root.Path, "packages", "trust.plugin", "1.0.0", ".signature.p7s")));
    }

    [Fact]
    public async Task ATamperedPlugin_IsRejectedEvenByTheDefaultPolicy()
    {
        var host = await LoadedHostAsync(TamperedFeed, new TrustPolicy());

        var result = await host.InstallAsync(Plugin, cancellationToken: Ct);

        Assert.Equal(InstallStatus.Failed, result.Status);
        Assert.Contains(result.Issues, i => i.Code == TrustIssueCodes.Tampered);
        AssertNothingInstalled();
    }

    [Fact]
    public async Task AFeedOutsideAllowedFeeds_IsNeverDownloadedFrom()
    {
        using var feed = new LocalNuGetFeed(UnsignedFeed);
        var host = await LoadedHostAsync(feed.ServiceIndex, new TrustPolicy { AllowedFeeds = { "https://nuget.example.com/v3/index.json" } });

        var result = await host.InstallAsync(Plugin, cancellationToken: Ct);

        Assert.Equal(InstallStatus.Failed, result.Status);
        Assert.Contains("the trust policy doesn't allow " + feed.ServiceIndex, result.Reason);
        Assert.Contains(result.Issues, i => i.Code == TrustIssueCodes.FeedNotAllowed);
        Assert.Empty(feed.Requests);   // not even the service index
        AssertNothingInstalled();
    }

    [Fact]
    public async Task APackageAlreadyInThePluginRoot_FromAFeedOutsideAllowedFeeds_IsRejected_WithAReasonToReinstall()
    {
        // Round 2, finding 4: AllowedFeeds is checked against the feed a reused package was downloaded from.
        var first = await LoadedHostAsync(UnsignedFeed, new TrustPolicy());
        Assert.Equal(InstallStatus.Loaded, (await first.InstallAsync(Plugin, cancellationToken: Ct)).Status);
        Assert.Equal(UnsignedFeed, new StateStore(_root.Path).Read().Find(Plugin)!.Source);
        await ForgetInstalledPluginsAsync();   // the package stays in the plugin root, orphaned; a new install reuses it
        var mirror = _feeds.Combine("mirror");
        Directory.CreateDirectory(mirror);
        File.Copy(Path.Combine(UnsignedFeed, $"{Plugin}.1.0.0.nupkg"), Path.Combine(mirror, $"{Plugin}.1.0.0.nupkg"));

        var notAllowed = await (await LoadedHostAsync(mirror, new TrustPolicy { AllowedFeeds = { mirror } })).InstallAsync(Plugin, cancellationToken: Ct);

        Assert.Equal(InstallStatus.Failed, notAllowed.Status);
        Assert.Contains($"(already in the plugin root): [{TrustIssueCodes.FeedNotAllowed}]", notAllowed.Reason);
        Assert.Contains($"downloaded from '{UnsignedFeed}'", notAllowed.Reason);
        Assert.Contains("Reinstall it from an allowed feed", notAllowed.Reason);
        Assert.Contains(notAllowed.Issues, i => i.Code == TrustIssueCodes.FeedNotAllowed && i.PluginId == Plugin);

        // Without a recorded source (state files and packages from before sources were recorded), it's unknown.
        File.WriteAllText(Path.Combine(_root.Path, "packages", "trust.plugin", "1.0.0", ".nupkg.metadata"),
            $$"""{ "version": 2, "contentHash": "{{PackageRecorder.ComputeContentHash(Path.Combine(UnsignedFeed, $"{Plugin}.1.0.0.nupkg"))}}" }""");
        var unknown = await (await LoadedHostAsync(mirror, new TrustPolicy { AllowedFeeds = { mirror } })).InstallAsync(Plugin, cancellationToken: Ct);
        Assert.Equal(InstallStatus.Failed, unknown.Status);
        Assert.Contains(unknown.Issues, i => i.Code == TrustIssueCodes.UnknownSource && i.PluginId == Plugin);

        // Without AllowedFeeds, the source isn't checked.
        var allowed = await (await LoadedHostAsync(mirror, new TrustPolicy())).InstallAsync(Plugin, cancellationToken: Ct);
        Assert.True(allowed.Status == InstallStatus.Loaded, allowed.ToString());
    }

    private void AssertNothingInstalled()
    {
        Assert.Empty(new StateStore(_root.Path).Read().Plugins);
        Assert.False(Directory.Exists(Path.Combine(_root.Path, "packages", "trust.plugin")), "A rejected package was extracted.");
    }

    private async Task ForgetInstalledPluginsAsync()
    {
        using var rootLock = await RootLock.AcquireAsync(_root.Path, cancellationToken: Ct);
        new StateStore(_root.Path).Write(rootLock, StateDocument.Empty);
    }

    private async Task<PluginHost> LoadedHostAsync(string feed, TrustPolicy policy)
    {
        var host = PluginHost.Create(new PluginHostOptions
        {
            Root = _root.Path,
            PluginPackageType = PackageType,
            Feeds = { new PackageFeed(feed) },
            TrustPolicy = policy,
        });
        _hosts.Add(host);
        await host.LoadAsync(Ct);
        return host;
    }

    private static void CreatePlugin(string path)
    {
        var builder = new PackageBuilder { Id = Plugin, Version = NuGetVersion.Parse("1.0.0"), Description = "Trust test plugin." };
        builder.Authors.Add("Alcove tests");
        builder.PackageTypes.Add(new PackageType(PackageType, new Version(1, 0)));
        builder.DependencyGroups.Add(new PackageDependencyGroup(NuGetFramework.Parse("net10.0"), []));
        builder.Files.Add(new PhysicalPackageFile(new MemoryStream("""{ "schemaVersion": 1 }"""u8.ToArray())) { TargetPath = PluginManifest.GetFileName(Plugin) });
        builder.Files.Add(new PhysicalPackageFile(new MemoryStream(RandomNumberGenerator.GetBytes(4096))) { TargetPath = "content/payload.bin" });
        using var stream = File.Create(path);
        builder.Save(stream);
    }
}

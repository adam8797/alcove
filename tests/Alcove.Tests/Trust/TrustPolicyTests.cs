using Alcove.Internal.Trust;
using NuGet.Versioning;
using Alcove.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using RevocationMode = NuGet.Common.RevocationMode;
using NuGet.Packaging.Signing;

namespace Alcove.Tests.Trust;

public sealed class TrustPolicyTests
{
    private const string Fingerprint = "1EEBEA645B50BBAC7B92E6AB78CD6C91D031F0A3EB65317163609A8D10580EF7";

    [Fact]
    public void Defaults()
    {
        var policy = new PluginHostOptions().TrustPolicy;

        Assert.Empty(policy.AllowedFeeds);
        Assert.False(policy.RequireSignedPackages);
        Assert.Empty(policy.AllowedSigners);
        Assert.Equal(RevocationCheck.Online, policy.Revocation);
    }

    [Fact]
    public void NuGetOrg_ExpandsToEveryKnownRepositoryCertificate()
    {
        var signers = AllowedSigner.NuGetOrg("jamesnk", "serilog").ToList();

        Assert.Equal(
            [
                "0E5F38F57DC1BCC806D8494F4F90FBCEDD988B46760709CBEEC6F4219AA6157D",
                "5A2901D6ADA3D18260B9C6DFE2133C95D74B9EEF6AE0E5DC334C8454D1477DF4",
                "1F4B311D9ACC115C8DC8018B5A49E00FCE6DA8E2855F9F014CA6F34570BC482D",
            ],
            signers.Select(s => s.Fingerprint));
        Assert.All(signers, s =>
        {
            Assert.Equal(SignerKind.Repository, s.Kind);
            Assert.Equal(["jamesnk", "serilog"], s.Owners);
            Assert.False(s.AllowUntrustedRoot);
            Assert.StartsWith("nuget.org", s.Name, StringComparison.Ordinal);
        });
        Assert.All(AllowedSigner.NuGetOrg(), s => Assert.Empty(s.Owners));
    }

    [Fact]
    public void SaveThenLoad_RoundTripsTheTrustPolicy()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("config.json");
        var options = new PluginHostOptions { Root = temp.Combine("root") };
        options.TrustPolicy.AllowedFeeds.Add("https://nuget.example.com/v3/index.json");
        options.TrustPolicy.RequireSignedPackages = true;
        options.TrustPolicy.Revocation = RevocationCheck.Offline;
        options.TrustPolicy.AllowedSigners.Add(new AllowedSigner { Kind = SignerKind.Author, Fingerprint = Fingerprint, AllowUntrustedRoot = true, Name = "Contoso" });
        foreach (var signer in AllowedSigner.NuGetOrg("jamesnk"))
        {
            options.TrustPolicy.AllowedSigners.Add(signer);
        }

        options.Save(path);
        var json = File.ReadAllText(path);
        var loaded = PluginHostOptions.Load(path).TrustPolicy;

        Assert.Contains("\"trustPolicy\"", json, StringComparison.Ordinal);
        Assert.Contains("\"revocation\": \"Offline\"", json, StringComparison.Ordinal);
        Assert.Contains("\"kind\": \"Repository\"", json, StringComparison.Ordinal);
        Assert.Equal(["https://nuget.example.com/v3/index.json"], loaded.AllowedFeeds);
        Assert.True(loaded.RequireSignedPackages);
        Assert.Equal(RevocationCheck.Offline, loaded.Revocation);
        Assert.Equal(4, loaded.AllowedSigners.Count);
        var author = loaded.AllowedSigners[0];
        Assert.Equal((SignerKind.Author, Fingerprint, true, "Contoso"), (author.Kind, author.Fingerprint, author.AllowUntrustedRoot, author.Name));
        Assert.Empty(author.Owners);
        Assert.Equal(["jamesnk"], loaded.AllowedSigners[3].Owners);
    }

    [Fact]
    public void Load_WithoutTrustPolicy_UsesTheDefault()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteFile("config.json", """{ "root": "/srv/plugins" }""");

        var policy = PluginHostOptions.Load(path).TrustPolicy;

        Assert.False(policy.RequireSignedPackages);
        Assert.Equal(RevocationCheck.Online, policy.Revocation);
    }

    [Fact]
    public void Load_ReadsAHandWrittenTrustPolicy()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteFile("config.json", $$"""
            {
              "root": "/srv/plugins",
              "trustPolicy": {
                "allowedFeeds": [ "/srv/feed" ],
                "requireSignedPackages": true,
                "allowedSigners": [ { "kind": "Author", "fingerprint": "{{Fingerprint}}", "allowUntrustedRoot": true } ],
                "revocation": "Offline"
              }
            }
            """);

        var policy = PluginHostOptions.Load(path).TrustPolicy;

        Assert.Equal(["/srv/feed"], policy.AllowedFeeds);
        Assert.True(policy.RequireSignedPackages);
        Assert.Equal(Fingerprint, Assert.Single(policy.AllowedSigners).Fingerprint);
        Assert.Equal(RevocationCheck.Offline, policy.Revocation);
    }

    [Fact]
    public void SettingNullCollections_GivesEmptyOnes()
    {
        var options = new PluginHostOptions { TrustPolicy = null! };
        var policy = new TrustPolicy { AllowedFeeds = null!, AllowedSigners = null! };

        Assert.Empty(options.TrustPolicy.AllowedFeeds);
        Assert.Empty(policy.AllowedFeeds);
        Assert.Empty(policy.AllowedSigners);
    }

    // --- Mapping onto NuGet's verifier settings -----------------------------------------------------------------

    [Theory]
    [InlineData(RevocationCheck.Online, RevocationMode.Online)]
    [InlineData(RevocationCheck.Offline, RevocationMode.Offline)]
    public void Settings_MapRevocation(RevocationCheck revocation, RevocationMode expected)
    {
        var settings = PackageVerifier.CreateSettings(requireSignedPackages: true, hasAllowedSigners: false, revocation);

        Assert.Equal(expected, settings.RevocationMode);
        Assert.True(settings.AllowUnknownRevocation);    // unknown status is a warning, never a failure
        Assert.True(settings.ReportUnknownRevocation);
    }

    [Theory]
    [InlineData(false, false, true, true)]    // NuGet "accept" mode: unsigned ok, untrusted chains ok (integrity still checked)
    [InlineData(false, true, true, false)]    // allowed signers: unsigned ok, signed packages must match
    [InlineData(true, false, false, false)]
    [InlineData(true, true, false, false)]
    public void Settings_MapSignatureRequirements(bool requireSigned, bool hasAllowedSigners, bool allowUnsigned, bool allowUntrusted)
    {
        var settings = PackageVerifier.CreateSettings(requireSigned, hasAllowedSigners, RevocationCheck.Online);

        Assert.Equal(allowUnsigned, settings.AllowUnsigned);
        Assert.Equal(allowUntrusted, settings.AllowUntrusted);
        Assert.False(settings.AllowIllegal);
        Assert.True(settings.AllowNoTimestamp);
        Assert.Equal(VerificationTarget.All, settings.VerificationTarget);
        Assert.Equal(SignaturePlacement.Any, settings.SignaturePlacement);
        Assert.Equal(SignatureVerificationBehavior.IfExistsAndIsNecessary, settings.RepositoryCountersignatureVerificationBehavior);
    }

    [Fact]
    public void Providers_AlwaysCheckIntegrity_AndAddTheAllowListOnlyForAllowedSigners()
    {
        Assert.Equal(
            [typeof(IntegrityVerificationProvider), typeof(SignatureTrustAndValidityVerificationProvider)],
            PackageVerifier.CreateProviders([]).Select(p => p.GetType()));
        Assert.Equal(
            [typeof(IntegrityVerificationProvider), typeof(SignatureTrustAndValidityVerificationProvider), typeof(AllowListVerificationProvider)],
            PackageVerifier.CreateProviders([new AllowedSigner { Fingerprint = Fingerprint }]).Select(p => p.GetType()));
    }

    // --- Policy validation -----------------------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("1EEB")]
    [InlineData("ZZEBEA645B50BBAC7B92E6AB78CD6C91D031F0A3EB65317163609A8D10580EF7")]
    public void InvalidFingerprint_MakesThePolicyRejectEverything(string fingerprint)
    {
        var verifier = Create(new TrustPolicy { AllowedSigners = { new AllowedSigner { Fingerprint = fingerprint } } });

        var decision = verifier.CheckFeed("https://nuget.example.com/v3/index.json");

        Assert.False(decision.Allowed);
        Assert.Equal(TrustIssueCodes.InvalidPolicy, Assert.Single(decision.Issues).Code);
    }

    [Fact]
    public void AuthorSignerWithOwners_IsInvalid()
    {
        var verifier = Create(new TrustPolicy { AllowedSigners = { new AllowedSigner { Kind = SignerKind.Author, Fingerprint = Fingerprint, Owners = ["contoso"] } } });

        var issue = Assert.Single(verifier.CheckFeed("/srv/feed").Issues);

        Assert.Equal(TrustIssueCodes.InvalidPolicy, issue.Code);
        Assert.Contains("owners only apply to repository signers", issue.Message, StringComparison.Ordinal);
    }

    // --- CheckFeed -------------------------------------------------------------------------------------------

    [Fact]
    public void CheckFeed_NoAllowedFeeds_AllowsAny()
    {
        var decision = Create(new TrustPolicy()).CheckFeed("https://anything.example.com/v3/index.json");

        Assert.True(decision.Allowed);
        Assert.False(decision.IsSigned);
        Assert.Empty(decision.Issues);
    }

    [Theory]
    [InlineData("https://nuget.example.com/v3/index.json", true)]
    [InlineData("HTTPS://NuGet.Example.com/v3/index.json", true)]    // scheme and host are case-insensitive
    [InlineData("https://nuget.example.com/v3/index.json/", true)]
    [InlineData("https://nuget.example.com:443/v3/index.json", true)]
    [InlineData(" https://nuget.example.com/v3/index.json ", true)]
    [InlineData("https://nuget.example.com/V3/index.json", false)]   // the path isn't
    [InlineData("http://nuget.example.com/v3/index.json", false)]
    [InlineData("https://evil.example.com/v3/index.json", false)]
    [InlineData("https://nuget.example.com/v3/index.json.evil", false)]
    public void CheckFeed_Urls(string source, bool allowed)
    {
        var verifier = Create(new TrustPolicy { AllowedFeeds = { "https://nuget.example.com/v3/index.json" } });

        var decision = verifier.CheckFeed(source);

        Assert.Equal(allowed, decision.Allowed);
        if (!allowed)
        {
            var issue = Assert.Single(decision.Issues);
            Assert.Equal((TrustIssueSeverity.Error, TrustIssueCodes.FeedNotAllowed), (issue.Severity, issue.Code));
        }
    }

    [Fact]
    public void CheckFeed_Folders()
    {
        using var temp = new TempDirectory();
        var feed = temp.Combine("feed");
        var verifier = Create(new TrustPolicy { AllowedFeeds = { feed } });

        Assert.True(verifier.CheckFeed(feed).Allowed);
        Assert.True(verifier.CheckFeed(feed + Path.DirectorySeparatorChar).Allowed);
        Assert.True(verifier.CheckFeed(Path.Combine(temp.Path, "other", "..", "feed")).Allowed);
        Assert.True(verifier.CheckFeed(new Uri(feed).AbsoluteUri).Allowed);   // file:// URL of the same folder
        Assert.False(verifier.CheckFeed(temp.Combine("feed2")).Allowed);
        Assert.False(verifier.CheckFeed(temp.Path).Allowed);
    }

    // --- Packages already in the plugin root ---------------------------------------------------------------

    [Fact]
    public void CheckKeptSource_NoAllowedFeeds_AllowsAnySource_EvenAnUnknownOne()
    {
        var verifier = Create(new TrustPolicy());

        Assert.True(verifier.CheckKeptSource("A", NuGetVersion.Parse("1.0.0"), null).Allowed);
        Assert.True(verifier.CheckKeptSource("A", NuGetVersion.Parse("1.0.0"), "/srv/anything").Allowed);
    }

    [Fact]
    public void CheckKeptSource_WithAllowedFeeds_RejectsAnUnknownOrOtherSource_SuggestingAReinstall()
    {
        // Round 2, finding 4.
        var verifier = Create(new TrustPolicy { AllowedFeeds = { "https://nuget.example.com/v3/index.json" } });
        var version = NuGetVersion.Parse("1.0.0");

        Assert.True(verifier.CheckKeptSource("A", version, "https://nuget.example.com/v3/index.json/").Allowed);

        var unknown = verifier.CheckKeptSource("A", version, null);
        Assert.False(unknown.Allowed);
        var issue = Assert.Single(unknown.Issues);
        Assert.Equal((TrustIssueSeverity.Error, TrustIssueCodes.UnknownSource), (issue.Severity, issue.Code));
        Assert.Contains("isn't recorded", issue.Message, StringComparison.Ordinal);
        Assert.Contains("Reinstall it from an allowed feed", issue.Message, StringComparison.Ordinal);

        var other = verifier.CheckKeptSource("A", version, "https://evil.example.com/v3/index.json");
        Assert.False(other.Allowed);
        issue = Assert.Single(other.Issues);
        Assert.Equal(TrustIssueCodes.FeedNotAllowed, issue.Code);
        Assert.Contains("downloaded from 'https://evil.example.com/v3/index.json'", issue.Message, StringComparison.Ordinal);
        Assert.Contains("Reinstall it from an allowed feed", issue.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(RevocationCheck.Online, false)]
    [InlineData(RevocationCheck.Offline, true)]
    public void Acceptances_AreRememberedOnlyWithOfflineRevocationChecks(RevocationCheck revocation, bool remembered)
    {
        // Round 2, finding 4: with online checks, a remembered acceptance would hide a revocation.
        Assert.Equal(remembered, Create(new TrustPolicy { Revocation = revocation }).RemembersAcceptances);
    }

    [Fact]
    public void TrustIssueCodes_ArePublicConstants_MatchingTheirNames()
    {
        var fields = typeof(TrustIssueCodes).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

        Assert.True(typeof(TrustIssueCodes).IsPublic);
        Assert.Contains(fields, f => f.Name == nameof(TrustIssueCodes.FeedNotAllowed));
        Assert.All(fields, f => Assert.Equal(f.Name, (string)f.GetRawConstantValue()!));
    }

    private static PackageVerifier Create(TrustPolicy policy) => new(policy, NullLogger.Instance);
}

using System.Globalization;
using Alcove.Storage;
using Microsoft.Extensions.Logging;
using NuGet.Common;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Packaging.Signing;
using NuGet.Versioning;
using HashAlgorithmName = NuGet.Common.HashAlgorithmName;
using ILogger = Microsoft.Extensions.Logging.ILogger;
using NuGetLogLevel = NuGet.Common.LogLevel;

namespace Alcove.Trust;

/// <summary>
/// Applies a <see cref="TrustPolicy"/> with NuGet's <see cref="PackageSignatureVerifier"/> (integrity, trust and
/// validity, allow-list providers), and explains every decision in its own words.
/// </summary>
/// <remarks>
/// The policy is copied when the verifier is created; later changes to the <see cref="TrustPolicy"/> object don't
/// affect it. NuGet's verdict (<c>IsValid</c>) is the decision; its issues only feed the explanation, because NuGet can
/// reject without an error and accept with one.
/// </remarks>
internal sealed class PackageVerifier : IPackageVerifier
{
    /// <summary>
    /// NuGet's trust store selection is process-wide, and the first caller wins (ADR 0012, documented exception):
    /// <see cref="X509TrustStore.InitializeForDotNetSdk"/> makes NuGet use the root bundle shipped next to
    /// <c>NuGet.Packaging.dll</c> on every OS. The value is what NuGet logged, for each verifier to report.
    /// This is the only static state in the trust code.
    /// </summary>
    private static readonly Lazy<IReadOnlyList<string>> TrustStoreInitialization = new(InitializeTrustStore);

    private readonly ILogger _logger;
    private readonly IReadOnlyList<FeedKey> _allowedFeeds;
    private readonly IReadOnlyList<TrustIssue> _policyErrors;
    private readonly SignedPackageVerifierSettings _settings;
    private readonly PackageSignatureVerifier _verifier;

    public PackageVerifier(TrustPolicy policy, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;

        var errors = new List<TrustIssue>();
        _allowedFeeds = ReadAllowedFeeds(policy.AllowedFeeds, errors);
        var signers = ReadAllowedSigners(policy.AllowedSigners, errors);
        _policyErrors = errors;
        PolicyFingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(policy, AlcoveJsonContext.Default.TrustPolicy)));
        _settings = CreateSettings(policy.RequireSignedPackages, signers.Count > 0, policy.Revocation);
        RemembersAcceptances = policy.Revocation == RevocationCheck.Offline;
        _verifier = new PackageSignatureVerifier(CreateProviders(signers));

        foreach (var error in errors)
        {
            TrustLog.InvalidPolicy(logger, error);
        }

        foreach (var message in TrustStoreInitialization.Value)
        {
            TrustLog.TrustStore(logger, message);
        }

        if (FindRootBundleProblem() is { } problem)
        {
            TrustLog.TrustStoreProblem(logger, problem);
        }
    }

    /// <inheritdoc/>
    /// <remarks>A hash of the policy as copied when the verifier was created.</remarks>
    public string PolicyFingerprint { get; }

    /// <inheritdoc/>
    public bool RemembersAcceptances { get; }

    /// <inheritdoc/>
    public TrustDecision CheckFeed(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (_policyErrors.Count > 0)
        {
            return Reject(source, isSigned: false, _policyErrors);
        }

        if (_allowedFeeds.Count == 0 || _allowedFeeds.Any(FeedKey.Create(source).Matches))
        {
            return new TrustDecision(true, false, []);
        }

        return Reject(source, isSigned: false,
        [
            new TrustIssue(TrustIssueSeverity.Error, TrustIssueCodes.FeedNotAllowed,
                $"The feed '{source}' isn't one of the trust policy's allowed feeds."),
        ]);

        TrustDecision Reject(string feed, bool isSigned, IReadOnlyList<TrustIssue> issues)
        {
            TrustLog.FeedRejected(_logger, feed, Describe(issues));
            return new TrustDecision(false, isSigned, issues);
        }
    }

    /// <inheritdoc/>
    public TrustDecision CheckKeptSource(string packageId, NuGetVersion version, string? source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentNullException.ThrowIfNull(version);
        if (_policyErrors.Count > 0)
        {
            return new TrustDecision(false, false, _policyErrors);
        }

        if (_allowedFeeds.Count == 0 || (source is { Length: > 0 } && _allowedFeeds.Any(FeedKey.Create(source).Matches)))
        {
            return new TrustDecision(true, false, []);
        }

        var package = $"{packageId} {version.ToNormalizedString()}";
        const string Remedy = "Reinstall it from an allowed feed: uninstall the plugins that use it, and once a restart has deleted it from the plugin root, install them again.";
        var issue = source is { Length: > 0 }
            ? new TrustIssue(TrustIssueSeverity.Error, TrustIssueCodes.FeedNotAllowed,
                $"{package} is already in the plugin root, downloaded from '{source}', which isn't one of the trust policy's allowed feeds. {Remedy}")
            : new TrustIssue(TrustIssueSeverity.Error, TrustIssueCodes.UnknownSource,
                $"{package} is already in the plugin root, but the feed it was downloaded from isn't recorded, so the trust policy's allowed feeds can't be checked. {Remedy}");
        TrustLog.FeedRejected(_logger, source ?? "(not recorded)", Describe([issue]));
        return new TrustDecision(false, false, [issue]);
    }

    /// <inheritdoc/>
    public async Task<TrustDecision> VerifyPackageAsync(string nupkgPath, string packageId, NuGetVersion version, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nupkgPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentNullException.ThrowIfNull(version);
        cancellationToken.ThrowIfCancellationRequested();

        var decision = _policyErrors.Count > 0
            ? new TrustDecision(false, false, _policyErrors)
            : await VerifyCoreAsync(nupkgPath, packageId, version, cancellationToken).ConfigureAwait(false);

        var versionText = version.ToNormalizedString();
        if (decision.Allowed)
        {
            TrustLog.PackageAccepted(_logger, packageId, versionText, decision.IsSigned,
                decision.Issues.Count == 0 ? "" : ": " + Describe(decision.Issues));
        }
        else
        {
            TrustLog.PackageRejected(_logger, packageId, versionText, decision.IsSigned, Describe(decision.Issues));
        }

        return decision;
    }

    /// <summary>NuGet's verifier settings for a policy: the mapping the signature spike verified.</summary>
    internal static SignedPackageVerifierSettings CreateSettings(bool requireSignedPackages, bool hasAllowedSigners, RevocationCheck revocation) => new(
        allowUnsigned: !requireSignedPackages,
        allowIllegal: false,                      // malformed signatures always fail
        // Signatures that are present are always integrity-checked. Chain trust only matters when the app asked for
        // signatures or named signers; otherwise behave like NuGet's "accept" mode.
        allowUntrusted: !requireSignedPackages && !hasAllowedSigners,
        allowIgnoreTimestamp: true,
        allowMultipleTimestamps: true,
        allowNoTimestamp: true,                   // timestamps are optional, as in NuGet's require mode
        allowUnknownRevocation: true,             // offline or unreachable revocation servers: a warning, not a failure
        reportUnknownRevocation: true,
        verificationTarget: VerificationTarget.All,
        signaturePlacement: SignaturePlacement.Any,
        repositoryCountersignatureVerificationBehavior: SignatureVerificationBehavior.IfExistsAndIsNecessary,
        revocationMode: revocation == RevocationCheck.Offline ? RevocationMode.Offline : RevocationMode.Online);

    /// <summary>
    /// NuGet's providers for a policy. An allowed signer's fingerprint goes both into the allow-list and, when it
    /// allows an untrusted root, into the trust provider; NuGet needs it in both places.
    /// </summary>
    internal static IReadOnlyList<ISignatureVerificationProvider> CreateProviders(IReadOnlyList<AllowedSigner> signers)
    {
        var providers = new List<ISignatureVerificationProvider>
        {
            new IntegrityVerificationProvider(),
            new SignatureTrustAndValidityVerificationProvider(signers
                .Where(s => s.AllowUntrustedRoot)
                .Select(s => new KeyValuePair<string, HashAlgorithmName>(s.Fingerprint, HashAlgorithmName.SHA256))
                .ToList()),
        };

        if (signers.Count > 0)
        {
            var allowList = signers.Select(s => (VerificationAllowListEntry)new TrustedSignerAllowListEntry(
                target: s.Kind == SignerKind.Author ? VerificationTarget.Author : VerificationTarget.Repository,
                placement: s.Kind == SignerKind.Author ? SignaturePlacement.PrimarySignature : SignaturePlacement.Any,
                fingerprint: s.Fingerprint,
                algorithm: HashAlgorithmName.SHA256,
                allowUntrustedRoot: s.AllowUntrustedRoot,
                owners: s.Owners.Count > 0 ? s.Owners.ToList() : null)).ToList();
            providers.Add(new AllowListVerificationProvider(allowList, requireNonEmptyAllowList: true,
                emptyListErrorMessage: "no allowed signers are configured",
                noMatchErrorMessage: "no signature matches an allowed signer"));
        }

        return providers;
    }

    private async Task<TrustDecision> VerifyCoreAsync(string nupkgPath, string packageId, NuGetVersion version, CancellationToken cancellationToken)
    {
        VerifySignaturesResult result;
        try
        {
            using var reader = new PackageArchiveReader(nupkgPath);
            var identity = reader.GetIdentity();
            if (!string.Equals(identity.Id, packageId, StringComparison.OrdinalIgnoreCase) || identity.Version != version)
            {
                return new TrustDecision(false, false,
                [
                    new TrustIssue(TrustIssueSeverity.Error, TrustIssueCodes.IdentityMismatch,
                        $"The downloaded package is {identity.Id} {identity.Version.ToNormalizedString()}, not the requested {packageId} {version.ToNormalizedString()}."),
                ]);
            }

            result = await _verifier.VerifySignaturesAsync(reader, _settings, cancellationToken, Guid.Empty).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidDataException or PackagingException or SignatureException)
        {
            return new TrustDecision(false, false,
            [
                new TrustIssue(TrustIssueSeverity.Error, TrustIssueCodes.Unreadable, $"The file isn't a readable package: {FirstLine(ex.Message)}"),
            ]);
        }

        return Explain(result);
    }

    /// <summary>Turns NuGet's result into our decision: <c>IsValid</c> decides; the issues explain.</summary>
    private static TrustDecision Explain(VerifySignaturesResult result)
    {
        var allowed = result.IsValid;
        var issues = new List<TrustIssue>();
        var seen = new HashSet<(string, string)>();
        foreach (var log in result.Results.SelectMany(r => r.Issues))
        {
            if (log.Level < NuGetLogLevel.Warning)
            {
                continue;
            }

            // NuGet reports errors on packages it accepts (e.g. an untrusted chain in "accept" mode): they didn't reject.
            var severity = log.Level >= NuGetLogLevel.Error && !allowed ? TrustIssueSeverity.Error : TrustIssueSeverity.Warning;
            var issue = Translate(log, severity);
            if (seen.Add((issue.Code, issue.Message)))
            {
                issues.Add(issue);
            }
        }

        if (!allowed && !issues.Any(i => i.Severity == TrustIssueSeverity.Error))
        {
            // NuGet can reject with no error at all (e.g. Trust=Disallowed only): say which check failed.
            var statuses = string.Join(", ", result.Results.Select((r, i) => $"{(result.IsSigned ? ProviderName(i) : "unsigned")}: {r.Trust}"));
            issues.Add(new TrustIssue(TrustIssueSeverity.Error, TrustIssueCodes.VerificationFailed,
                $"The package's signature was rejected without a specific reason ({(statuses.Length > 0 ? statuses : "no results")})."));
        }

        if (allowed && !result.IsSigned)
        {
            issues.Add(new TrustIssue(TrustIssueSeverity.Info, TrustIssueCodes.UnsignedAccepted,
                "The package isn't signed; accepted because the trust policy doesn't require signed packages."));
        }

        return new TrustDecision(allowed, result.IsSigned, issues);
    }

    private static TrustIssue Translate(SignatureLog log, TrustIssueSeverity severity)
    {
        var nuget = $"({log.Code}: {FirstLine(log.Message)})";
        var rejecting = severity == TrustIssueSeverity.Error;
        var (code, text) = log.Code switch
        {
            NuGetLogCode.NU3004 => (TrustIssueCodes.NotSigned, "The package isn't signed, and the trust policy requires signed packages."),
            NuGetLogCode.NU3008 => (TrustIssueCodes.Tampered, "The package has changed since it was signed."),
            NuGetLogCode.NU3034 => (TrustIssueCodes.SignerNotAllowed, "The package isn't signed by any of the trust policy's allowed signers."),
            NuGetLogCode.NU3018 when log.Message.Contains("revocation", StringComparison.OrdinalIgnoreCase) =>
                (TrustIssueCodes.RevocationUnknown, "A signing certificate's revocation status couldn't be checked."),
            NuGetLogCode.NU3018 => (TrustIssueCodes.UntrustedCertificate, rejecting
                ? "A signing certificate doesn't chain to a trusted root. A self-signed or private-CA signer must be an allowed signer with AllowUntrustedRoot."
                : "A signing certificate doesn't chain to a trusted root; accepted because the trust policy doesn't require trusted signatures."),
            NuGetLogCode.NU3027 => (TrustIssueCodes.NotTimestamped, "A signature has no timestamp, so it stops being valid when its certificate expires."),
            NuGetLogCode.NU3036 or NuGetLogCode.NU3037 => (TrustIssueCodes.CertificateExpired, "A signing certificate has expired or isn't valid yet, and no valid timestamp covers it."),
            >= NuGetLogCode.NU3020 and <= NuGetLogCode.NU3029 =>
                (TrustIssueCodes.InvalidTimestamp, "A signature's timestamp is invalid or untrusted."),
            _ => (TrustIssueCodes.InvalidSignature, rejecting ? "The package's signature is invalid." : "The package's signature has a problem."),
        };

        return new TrustIssue(severity, code, $"{text} {nuget}");
    }

    /// <summary>For a signed package NuGet returns one result per provider, in the providers' order (see <see cref="CreateProviders"/>).</summary>
    private static string ProviderName(int index) => index switch
    {
        0 => "integrity",
        1 => "certificate trust",
        2 => "allowed signers",
        _ => $"check {index + 1}",
    };

    private static IReadOnlyList<FeedKey> ReadAllowedFeeds(IEnumerable<string>? feeds, List<TrustIssue> errors)
    {
        var keys = new List<FeedKey>();
        foreach (var feed in feeds ?? [])
        {
            if (string.IsNullOrWhiteSpace(feed))
            {
                errors.Add(new TrustIssue(TrustIssueSeverity.Error, TrustIssueCodes.InvalidPolicy, "AllowedFeeds contains an empty entry."));
                continue;
            }

            keys.Add(FeedKey.Create(feed));
        }

        return keys;
    }

    /// <summary>Validates the allowed signers and normalises their fingerprints (upper-case hex, no separators).</summary>
    private static IReadOnlyList<AllowedSigner> ReadAllowedSigners(IEnumerable<AllowedSigner?>? signers, List<TrustIssue> errors)
    {
        var result = new List<AllowedSigner>();
        foreach (var signer in signers ?? [])
        {
            if (signer is null)
            {
                errors.Add(new TrustIssue(TrustIssueSeverity.Error, TrustIssueCodes.InvalidPolicy, "AllowedSigners contains an empty entry."));
                continue;
            }

            var label = signer.Name ?? signer.Fingerprint;
            var fingerprint = string.Concat((signer.Fingerprint ?? "").Where(c => c is not (':' or ' ' or '-'))).ToUpperInvariant();
            if (fingerprint.Length != 64 || !fingerprint.All(char.IsAsciiHexDigit))
            {
                errors.Add(new TrustIssue(TrustIssueSeverity.Error, TrustIssueCodes.InvalidPolicy,
                    $"Allowed signer '{label}': '{signer.Fingerprint}' isn't a SHA-256 certificate fingerprint (64 hex digits)."));
                continue;
            }

            if (!Enum.IsDefined(signer.Kind))
            {
                errors.Add(new TrustIssue(TrustIssueSeverity.Error, TrustIssueCodes.InvalidPolicy,
                    $"Allowed signer '{label}' has an unknown kind '{signer.Kind.ToString()}'."));
                continue;
            }

            var owners = (signer.Owners ?? []).ToList();
            if (owners.Any(string.IsNullOrWhiteSpace))
            {
                errors.Add(new TrustIssue(TrustIssueSeverity.Error, TrustIssueCodes.InvalidPolicy, $"Allowed signer '{label}' has an empty owner."));
                continue;
            }

            if (signer.Kind == SignerKind.Author && owners.Count > 0)
            {
                // Owners are only in repository signatures. Ignoring them would silently accept more than intended.
                errors.Add(new TrustIssue(TrustIssueSeverity.Error, TrustIssueCodes.InvalidPolicy,
                    $"Allowed signer '{label}' is an author certificate but lists owners; owners only apply to repository signers."));
                continue;
            }

            result.Add(signer with { Fingerprint = fingerprint, Owners = owners });
        }

        return result;
    }

    private static IReadOnlyList<string> InitializeTrustStore()
    {
        var log = new CollectingLogger();
        try
        {
            X509TrustStore.InitializeForDotNetSdk(log);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            // Single-file apps (unsupported) fail here; NuGet then keeps .NET's default trust store.
            log.Messages.Add($"couldn't select NuGet's bundled roots ({ex.GetType().Name}: {FirstLine(ex.Message)}); using .NET's default trust store");
        }

        return log.Messages;
    }

    /// <summary>Why NuGet won't find the root bundle next to <c>NuGet.Packaging.dll</c>, or <see langword="null"/> if it will.</summary>
    internal static string? FindRootBundleProblem()
    {
        var location = typeof(PackageArchiveReader).Assembly.Location;
        if (string.IsNullOrEmpty(location))
        {
            return "NuGet.Packaging.dll isn't loaded from a file (single-file apps are unsupported), so its root bundle can't be found";
        }

        var directory = Path.Combine(Path.GetDirectoryName(location)!, "trustedroots");
        var missing = new[] { "codesignctl.pem", "timestampctl.pem" }.Where(f => !File.Exists(Path.Combine(directory, f))).ToList();
        return missing.Count == 0
            ? null
            : string.Create(CultureInfo.InvariantCulture, $"{string.Join(" and ", missing)} missing from '{directory}' (the Alcove package copies them to the app's output)");
    }

    private static string Describe(IEnumerable<TrustIssue> issues) => string.Join("; ", issues.Select(i => $"{i.Code}: {i.Message}"));

    private static string FirstLine(string message) => message.Split('\n')[0].Trim();

    /// <summary>A feed source normalised for comparison: a URL (case-sensitive path) or a full local path.</summary>
    private readonly record struct FeedKey(bool IsUrl, string Value)
    {
        public static FeedKey Create(string source)
        {
            var trimmed = source.Trim();
            if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && !uri.IsFile)
            {
                return new FeedKey(true, uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.PathAndQuery, UriFormat.UriEscaped).TrimEnd('/'));
            }

            var path = uri is { IsFile: true } ? uri.LocalPath : trimmed;
            return new FeedKey(false, Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)));
        }

        public bool Matches(FeedKey other) =>
            IsUrl == other.IsUrl && string.Equals(Value, other.Value,
                !IsUrl && (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    /// <summary>Captures what NuGet logs while selecting the trust store.</summary>
    private sealed class CollectingLogger : LoggerBase
    {
        public List<string> Messages { get; } = [];

        public override void Log(ILogMessage message)
        {
            lock (Messages)
            {
                Messages.Add(FirstLine(message.Message));
            }
        }

        public override Task LogAsync(ILogMessage message)
        {
            Log(message);
            return Task.CompletedTask;
        }
    }
}

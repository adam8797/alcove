// Spike: NuGet package signature verification with NuGet.Packaging (throwaway code).
//
// Usage:
//   Verifier inspect <nupkg>...                      print signature details (type, cert fingerprints, owners, timestamp)
//   Verifier matrix  [--trust-store default|sdk] [--revocation online|offline] [--diag]
//                                                    run every test package against every test policy
//   Verifier time    <nupkg> [n]                      time n verifications of one package (require-signed policy)
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using NuGet.Common;
using NuGet.Packaging;
using NuGet.Packaging.Signing;
using HashAlgorithmName = NuGet.Common.HashAlgorithmName;

var spikeDir = FindSpikeDir();
var opts = Options.Parse(args.Skip(1).ToArray());
var log = new ConsoleLogger();

// ---- Trust store selection (process-global in NuGet!) ---------------------------------------
if (opts.TrustStore == "sdk")
{
    // The only public knob: use the fallback bundle "trustedroots/*.pem" next to NuGet.Packaging.dll.
    X509TrustStore.InitializeForDotNetSdk(log);
}
if (opts.Diag) Diagnostics.PrintTrustStore(log);

Console.WriteLine($"# OS={System.Runtime.InteropServices.RuntimeInformation.OSDescription} runtime={Environment.Version} " +
                  $"NuGet.Packaging={typeof(PackageArchiveReader).Assembly.GetName().Version} trust-store={opts.TrustStore} revocation={opts.Revocation}");
Console.WriteLine($"# NuGet.Packaging.dll dir: {Path.GetDirectoryName(typeof(PackageArchiveReader).Assembly.Location)}");

switch (args.FirstOrDefault())
{
    case "inspect":
        foreach (var p in opts.Positional) await Inspect.RunAsync(p);
        break;
    case "time":
        await Timing.RunAsync(opts.Positional[0], opts.Positional.Count > 1 ? int.Parse(opts.Positional[1]) : 20, opts);
        break;
    case "defaults":
        // What NuGet's own policies look like, and whether NUGET_CERT_REVOCATION_MODE is honoured.
        foreach (var (n, st) in new[] {
            ("GetDefault", SignedPackageVerifierSettings.GetDefault()),
            ("GetAcceptModeDefaultPolicy", SignedPackageVerifierSettings.GetAcceptModeDefaultPolicy()),
            ("GetRequireModeDefaultPolicy", SignedPackageVerifierSettings.GetRequireModeDefaultPolicy()),
            ("GetVerifyCommandDefaultPolicy", SignedPackageVerifierSettings.GetVerifyCommandDefaultPolicy()) })
            Console.WriteLine($"{n,-30} unsigned={st.AllowUnsigned} illegal={st.AllowIllegal} untrusted={st.AllowUntrusted} noTimestamp={st.AllowNoTimestamp} " +
                              $"unknownRevocation={st.AllowUnknownRevocation} reportUnknownRev={st.ReportUnknownRevocation} target={st.VerificationTarget} " +
                              $"placement={st.SignaturePlacement} counter={st.RepositoryCountersignatureVerificationBehavior} revocation={st.RevocationMode}");
        break;
    case "nugetconfig":
        await NuGetConfigDemo.RunAsync(spikeDir, opts);
        break;
    case "matrix":
    default:
        await Matrix.RunAsync(spikeDir, opts);
        break;
}

static string FindSpikeDir()
{
    var d = new DirectoryInfo(AppContext.BaseDirectory);
    while (d != null && !File.Exists(Path.Combine(d.FullName, "setup.sh"))) d = d.Parent;
    return d?.FullName ?? throw new InvalidOperationException("spike dir not found");
}

// =================================================================================================
// The proposed TrustPolicy shape (prototype) and its mapping onto NuGet.Packaging.Signing.
// =================================================================================================

public enum SignerKind { Author, Repository }
public enum RevocationCheck { Online, Offline }

/// <summary>One allowed signer. Mirrors NuGet's &lt;trustedSigners&gt; model (author / repository + owners).</summary>
public sealed record AllowedSigner(
    SignerKind Kind,
    string Sha256Fingerprint,                 // SHA-256 of the signing certificate (leaf), hex
    IReadOnlyList<string>? Owners = null,     // Repository only: package must have >= 1 of these nuget.org owners
    bool AllowUntrustedRoot = false,          // accept even if the chain does not end in a trusted root (self-signed / private CA)
    string? Name = null);

public sealed record TrustPolicy
{
    /// <summary>Unsigned packages are rejected. Signed packages are always integrity-checked regardless.</summary>
    public bool RequireSignedPackages { get; init; }
    /// <summary>If non-empty, at least one signature (author primary or repository primary/countersignature) must match one entry.</summary>
    public IReadOnlyList<AllowedSigner> AllowedSigners { get; init; } = [];
    /// <summary>Online: revocation via CRL/OCSP; unreachable => warning (NU3018 RevocationStatusUnknown) not failure. Offline: cached CRLs only.</summary>
    public RevocationCheck Revocation { get; init; } = RevocationCheck.Online;
    /// <summary>Experiment only: fail when revocation status cannot be determined (NuGet's AllowUnknownRevocation=false).</summary>
    public bool RequireRevocationStatus { get; init; }
}

public sealed record VerifyOutcome(bool Accepted, bool IsSigned, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings, TimeSpan Elapsed);

public static class PolicyVerifier
{
    public static async Task<VerifyOutcome> VerifyAsync(string nupkgPath, TrustPolicy policy, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        var settings = new SignedPackageVerifierSettings(
            allowUnsigned: !policy.RequireSignedPackages,
            allowIllegal: false,                  // malformed signatures always fail
            // Signatures that are present are always integrity-checked. Chain trust only matters when the app asked for
            // signatures (RequireSignedPackages) or named signers; otherwise behave like NuGet's "accept" mode.
            allowUntrusted: !policy.RequireSignedPackages && policy.AllowedSigners.Count == 0,
            allowIgnoreTimestamp: true,
            allowMultipleTimestamps: true,
            allowNoTimestamp: true,               // timestamp is optional (as in NuGet's require mode)
            allowUnknownRevocation: !policy.RequireRevocationStatus, // offline / unreachable CRL => warning
            reportUnknownRevocation: true,
            verificationTarget: VerificationTarget.All,
            signaturePlacement: SignaturePlacement.Any,
            repositoryCountersignatureVerificationBehavior: SignatureVerificationBehavior.IfExistsAndIsNecessary,
            revocationMode: policy.Revocation == RevocationCheck.Online ? RevocationMode.Online : RevocationMode.Offline);

        var allowList = policy.AllowedSigners.Select(s => (VerificationAllowListEntry)new TrustedSignerAllowListEntry(
            target: s.Kind == SignerKind.Author ? VerificationTarget.Author : VerificationTarget.Repository,
            placement: s.Kind == SignerKind.Author ? SignaturePlacement.PrimarySignature : SignaturePlacement.Any,
            fingerprint: s.Sha256Fingerprint,
            algorithm: HashAlgorithmName.SHA256,
            allowUntrustedRoot: s.AllowUntrustedRoot,
            owners: s.Owners)).ToList();

        var untrustedRootAllowed = policy.AllowedSigners.Where(s => s.AllowUntrustedRoot)
            .Select(s => new KeyValuePair<string, HashAlgorithmName>(s.Sha256Fingerprint, HashAlgorithmName.SHA256)).ToList();

        var providers = new List<ISignatureVerificationProvider>
        {
            new IntegrityVerificationProvider(),
            new SignatureTrustAndValidityVerificationProvider(untrustedRootAllowed),
        };
        if (allowList.Count > 0)
            providers.Add(new AllowListVerificationProvider(allowList, requireNonEmptyAllowList: true,
                emptyListErrorMessage: "no allowed signers configured",
                noMatchErrorMessage: "package is not signed by an allowed signer"));

        using var reader = new PackageArchiveReader(nupkgPath);
        var result = await new PackageSignatureVerifier(providers).VerifySignaturesAsync(reader, settings, ct, Guid.Empty);

        var issues = result.Results.SelectMany(r => r.Issues).ToList();
        string Fmt(SignatureLog l) => $"{l.Code}: {l.Message.Split('\n')[0].Trim()}";
        var errors = issues.Where(i => i.Level == LogLevel.Error).Select(Fmt).Distinct().ToList();
        if (!result.IsValid && errors.Count == 0) // NuGet can reject without an error-level issue; surface the per-provider status
            errors.Add("(no error issued) provider trust: " + string.Join(", ", result.Results.Select(r => $"{r.GetType().Name}={r.Trust}")) +
                       "; other issues: " + string.Join(" | ", issues.Select(i => $"{i.Level}/{Fmt(i)}")));
        return new VerifyOutcome(
            result.IsValid, result.IsSigned,
            errors,
            issues.Where(i => i.Level == LogLevel.Warning).Select(Fmt).Distinct().ToList(),
            sw.Elapsed);
    }
}

// =================================================================================================
// Experiments
// =================================================================================================

public static class Matrix
{
    public static async Task RunAsync(string spikeDir, Options opts)
    {
        string F(string rel) => Path.Combine(spikeDir, rel);
        string Fp(string name) => File.ReadAllText(F($"certs/{name}.sha256")).Trim();
        var nugetOrgRepoCerts = JsonDocument.Parse(File.ReadAllText(F("downloaded/nugetorg-repository-signatures.json")))
            .RootElement.GetProperty("signingCertificates").EnumerateArray()
            .Select(c => c.GetProperty("fingerprints").GetProperty("2.16.840.1.101.3.4.2.1").GetString()!).ToList();

        var packages = new (string Label, string Path)[]
        {
            ("nuget.org author+repo: Newtonsoft.Json 13.0.3", F("downloaded/newtonsoft.json.13.0.3.nupkg")),
            ("nuget.org author+repo: Humanizer.Core 2.14.1", F("downloaded/humanizer.core.2.14.1.nupkg")),
            ("nuget.org repo-only:   Serilog 4.0.0", F("downloaded/serilog.4.0.0.nupkg")),
            ("nuget.org repo-only:   Dapper 2.1.35", F("downloaded/dapper.2.1.35.nupkg")),
            ("local unsigned", F("feed/unsigned/Spike.SamplePlugin.1.0.0.nupkg")),
            ("local self-signed (allow-listed cert)", F("feed/signed-selfsigned/Spike.SamplePlugin.1.0.0.nupkg")),
            ("local self-signed (other cert)", F("feed/signed-other/Spike.SamplePlugin.1.0.0.nupkg")),
            ("TAMPERED self-signed", F("feed/tampered/selfsigned-tampered.nupkg")),
            ("TAMPERED Newtonsoft.Json", F("feed/tampered/newtonsoft-tampered.nupkg")),
        };

        var rev = opts.Revocation;
        var policies = new (string Name, TrustPolicy Policy)[]
        {
            ("P0 none (signatures optional)", new TrustPolicy { Revocation = rev }),
            ("P1 require signed, any trusted signer", new TrustPolicy { RequireSignedPackages = true, Revocation = rev }),
            ("P2 require signed, allow: test author cert (untrusted root ok)", new TrustPolicy
            {
                RequireSignedPackages = true, Revocation = rev,
                AllowedSigners = [new(SignerKind.Author, Fp("author-test"), AllowUntrustedRoot: true, Name: "Spike Test Author")],
            }),
            ("P3 require signed, allow: test author cert WITHOUT untrusted-root flag", new TrustPolicy
            {
                RequireSignedPackages = true, Revocation = rev,
                AllowedSigners = [new(SignerKind.Author, Fp("author-test"))],
            }),
            ("P4 require signed, allow: nuget.org repo (any owner)", new TrustPolicy
            {
                RequireSignedPackages = true, Revocation = rev,
                AllowedSigners = nugetOrgRepoCerts.Select(fp => new AllowedSigner(SignerKind.Repository, fp, Name: "nuget.org")).ToList(),
            }),
            ("P5 require signed, allow: nuget.org repo with owners [jamesnk, serilog]", new TrustPolicy
            {
                RequireSignedPackages = true, Revocation = rev,
                AllowedSigners = nugetOrgRepoCerts.Select(fp => new AllowedSigner(SignerKind.Repository, fp, Owners: ["jamesnk", "serilog"])).ToList(),
            }),
            ("P6 require signed, allow: Json.NET author cert only", new TrustPolicy
            {
                RequireSignedPackages = true, Revocation = rev,
                AllowedSigners = [new(SignerKind.Author, "D60F9A9BD35829CCF4684D4B1E0B62EB3D982FCD55159F337348BB18EA25B8A7", Name: "Json.NET (.NET Foundation)")],
            }),
            ("P7 not required but allow-list set: test author + nuget.org", new TrustPolicy
            {
                RequireSignedPackages = false, Revocation = rev,
                AllowedSigners = [new(SignerKind.Author, Fp("author-test"), AllowUntrustedRoot: true),
                                  .. nugetOrgRepoCerts.Select(fp => new AllowedSigner(SignerKind.Repository, fp))],
            }),
        };

        foreach (var (pname, policy) in policies)
        {
            Console.WriteLine();
            Console.WriteLine($"## {pname}");
            foreach (var (label, path) in packages)
            {
                VerifyOutcome o;
                try { o = await PolicyVerifier.VerifyAsync(path, policy); }
                catch (Exception ex) { Console.WriteLine($"  {label,-48} EXCEPTION {ex.GetType().Name}: {ex.Message}"); continue; }
                Console.WriteLine($"  {label,-48} {(o.Accepted ? "ACCEPT" : "REJECT"),-6} signed={o.IsSigned,-5} {o.Elapsed.TotalMilliseconds,7:F1} ms");
                foreach (var e in o.Errors) Console.WriteLine($"      error {e}");
                if (opts.ShowWarnings) foreach (var w in o.Warnings) Console.WriteLine($"      warn  {w}");
                else if (o.Warnings.Count > 0) Console.WriteLine($"      warn  {string.Join(", ", o.Warnings.Select(w => w.Split(':')[0] + (w.Contains("Revocation") || w.Contains("revocation") ? "(revocation)" : "")).Distinct())}");
            }
        }
    }
}

public static class NuGetConfigDemo
{
    // Q4: can NuGet's own <trustedSigners> model be reused? Load it with ClientPolicyContext and verify with it.
    public static async Task RunAsync(string spikeDir, Options opts)
    {
        var template = File.ReadAllText(Path.Combine(spikeDir, "trustedsigners-demo/nuget.config"));
        var dir = Directory.CreateTempSubdirectory("sigspike-cfg").FullName;
        File.WriteAllText(Path.Combine(dir, "nuget.config"),
            template.Replace("__AUTHOR_FP__", File.ReadAllText(Path.Combine(spikeDir, "certs/author-test.sha256")).Trim()));
        var settings = NuGet.Configuration.Settings.LoadSpecificSettings(dir, "nuget.config");
        var ctx = ClientPolicyContext.GetClientPolicy(settings, NullLogger.Instance);
        Console.WriteLine($"policy={ctx.Policy} allowList={ctx.AllowList.Count}");
        foreach (var e in ctx.AllowList)
            Console.WriteLine($"  target={e.Target} placement={e.Placement} fp={e.Fingerprint[..16]}... untrustedRoot={e.AllowUntrustedRoot} owners=[{string.Join(",", e.Owners ?? [])}]");
        // Note: this is exactly what restore does; we feed ClientPolicyContext into the same providers.
        var providers = new ISignatureVerificationProvider[]
        {
            new IntegrityVerificationProvider(),
            new SignatureTrustAndValidityVerificationProvider(ctx.AllowList.Where(e => e.AllowUntrustedRoot)
                .Select(e => new KeyValuePair<string, HashAlgorithmName>(e.Fingerprint, e.FingerprintAlgorithm)).ToList()),
            new AllowListVerificationProvider(ctx.AllowList.Cast<VerificationAllowListEntry>().ToList(), ctx.Policy == SignatureValidationMode.Require,
                "empty", "no match"),
        };
        foreach (var p in new[] { "downloaded/newtonsoft.json.13.0.3.nupkg", "downloaded/humanizer.core.2.14.1.nupkg", "downloaded/serilog.4.0.0.nupkg",
                                  "feed/signed-selfsigned/Spike.SamplePlugin.1.0.0.nupkg", "feed/signed-other/Spike.SamplePlugin.1.0.0.nupkg", "feed/unsigned/Spike.SamplePlugin.1.0.0.nupkg" })
        {
            using var r = new PackageArchiveReader(Path.Combine(spikeDir, p));
            var res = await new PackageSignatureVerifier(providers).VerifySignaturesAsync(r, ctx.VerifierSettings, CancellationToken.None, Guid.Empty);
            Console.WriteLine($"  {p,-55} {(res.IsValid ? "ACCEPT" : "REJECT")} {string.Join("; ", res.Results.SelectMany(x => x.Issues).Where(i => i.Level == LogLevel.Error).Select(i => i.Code).Distinct())}");
        }
        Directory.Delete(dir, true);
    }
}

public static class Inspect
{
    public static async Task RunAsync(string path)
    {
        using var reader = new PackageArchiveReader(path);
        Console.WriteLine($"\n== {Path.GetFileName(path)}  id={reader.GetIdentity()}");
        if (!await reader.IsSignedAsync(CancellationToken.None)) { Console.WriteLine("   unsigned"); return; }
        var primary = await reader.GetPrimarySignatureAsync(CancellationToken.None);
        Print("primary", primary);
        if (primary is IRepositorySignature rp) Console.WriteLine($"   owners={string.Join(",", rp.PackageOwners ?? [])} serviceIndex={rp.V3ServiceIndexUrl}");
        var counter = RepositoryCountersignature.GetRepositoryCountersignature(primary);
        if (counter != null)
        {
            Print("repository countersignature", counter);
            Console.WriteLine($"   owners={string.Join(",", counter.PackageOwners ?? [])} serviceIndex={counter.V3ServiceIndexUrl}");
        }

        static void Print(string what, Signature s)
        {
            var c = s.SignerInfo.Certificate;
            Console.WriteLine($"   {what}: type={s.Type} subject='{c.Subject}'");
            Console.WriteLine($"      signer SHA-256={c.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256)} valid {c.NotBefore:yyyy-MM-dd}..{c.NotAfter:yyyy-MM-dd}");
            foreach (var t in s.Timestamps) Console.WriteLine($"      timestamp {t.GeneralizedTime:u} by '{t.SignerInfo.Certificate.Subject}'");
            if (s.Timestamps.Count == 0) Console.WriteLine("      (no timestamp)");
        }
    }
}

public static class Timing
{
    public static async Task RunAsync(string path, int n, Options opts)
    {
        var policy = new TrustPolicy { RequireSignedPackages = true, Revocation = opts.Revocation, RequireRevocationStatus = opts.StrictRevocation };
        var times = new List<double>();
        for (int i = 0; i < n; i++)
        {
            using var cts = opts.TimeoutMs > 0 ? new CancellationTokenSource(opts.TimeoutMs) : new CancellationTokenSource();
            VerifyOutcome o;
            var sw0 = Stopwatch.StartNew();
            try { o = await PolicyVerifier.VerifyAsync(path, policy, cts.Token); }
            catch (OperationCanceledException) { Console.WriteLine($"cancelled after {sw0.Elapsed.TotalMilliseconds:F0} ms"); continue; }
            times.Add(o.Elapsed.TotalMilliseconds);
            if (i == 0) Console.WriteLine($"first: {o.Elapsed.TotalMilliseconds:F1} ms accepted={o.Accepted} errors=[{string.Join("; ", o.Errors)}] warnings=[{string.Join("; ", o.Warnings)}]");
        }
        if (times.Count == 0) return;
        var warm = times.Skip(1).OrderBy(x => x).ToList();
        if (warm.Count == 0) { Console.WriteLine($"{Path.GetFileName(path)}: single run {times[0]:F1} ms"); return; }
        Console.WriteLine($"{Path.GetFileName(path)} ({new FileInfo(path).Length / 1024} KiB): first {times[0]:F1} ms; warm n={warm.Count} median {warm[warm.Count / 2]:F1} ms min {warm[0]:F1} max {warm[^1]:F1}");
    }
}

public static class Diagnostics
{
    // Diagnostic only: ask NuGet (internal API, via reflection) which chain factory it would use by default.
    public static void PrintTrustStore(ILogger log)
    {
        var t = typeof(X509TrustStore);
        foreach (var purpose in new[] { "CodeSigning", "Timestamping" })
        {
            var purposeType = t.Assembly.GetType("NuGet.Packaging.Signing.X509StorePurpose")!;
            var p = Enum.Parse(purposeType, purpose);
            var create = t.GetMethod("CreateX509ChainFactory", BindingFlags.NonPublic | BindingFlags.Static, [purposeType, typeof(ILogger)])!;
            var f = create.Invoke(null, [p, log]);
            Console.WriteLine($"# [diag] default chain factory for {purpose}: {f?.GetType().Name}");
        }
    }
}

public sealed class Options
{
    public string TrustStore = "default";
    public RevocationCheck Revocation = RevocationCheck.Online;
    public bool Diag, ShowWarnings, StrictRevocation;
    public int TimeoutMs;
    public List<string> Positional = [];
    public static Options Parse(string[] a)
    {
        var o = new Options();
        for (int i = 0; i < a.Length; i++)
        {
            switch (a[i])
            {
                case "--trust-store": o.TrustStore = a[++i]; break;
                case "--revocation": o.Revocation = Enum.Parse<RevocationCheck>(a[++i], true); break;
                case "--diag": o.Diag = true; break;
                case "--warnings": o.ShowWarnings = true; break;
                case "--strict-revocation": o.StrictRevocation = true; break;
                case "--timeout-ms": o.TimeoutMs = int.Parse(a[++i]); break;
                default: o.Positional.Add(a[i]); break;
            }
        }
        return o;
    }
}

public sealed class ConsoleLogger : LoggerBase
{
    public override void Log(ILogMessage message) => Console.WriteLine($"# [nuget-{message.Level}] {message.Message.Split('\n')[0]}");
    public override Task LogAsync(ILogMessage message) { Log(message); return Task.CompletedTask; }
}

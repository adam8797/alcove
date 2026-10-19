# Spike: NuGet package signature verification

## Goal
Determine how the trust policy (`RequireSignedPackages`, `AllowedSigners`, allowed feeds) can be implemented with the NuGet client libraries, and whether it works on Linux (console/web hosts) as well as Windows.

## Questions
1. Which API verifies a package signature programmatically (`NuGet.Packaging.Signing`: `PackageSignatureVerifier`, `SignatureTrustAndValidityVerificationProvider`, `SignedPackageVerifierSettings`, `ClientPolicyContext`)? What does `dotnet nuget verify` use?
2. On Linux with .NET 10: does verification of a nuget.org package (repository-signed; many also author-signed, e.g. `Newtonsoft.Json`) succeed? Which certificate trust store is used, and does it need the SDK's fallback certificate bundle? Does that bundle exist for a plain runtime-only app (no SDK installed)?
3. Author signing with a **self-signed test certificate** (create it with `openssl` or `System.Security.Cryptography.X509Certificates`; sign with `dotnet nuget sign`): can we verify it, and allow-list it by SHA-256 fingerprint, without installing the certificate in the system store?
4. How do we express "allowed signers": author certificate fingerprints, repository signature + owners (nuget.org `owners` in the repository countersignature), both? What does NuGet's own `trustedSigners` in `nuget.config` support, and can we reuse that model?
5. Timestamps and revocation checks: what happens offline? Can revocation checking be disabled or set to "best effort" (`NUGET_CERT_REVOCATION_MODE=offline`) through the API?
6. Unsigned packages from a local folder feed: correctly reported as unsigned?
7. Timing: how long does verification take per package?

## Experiments
- A console app verifying: a nuget.org package (download it), a self-signed package, an unsigned package, a tampered signed package (modify a byte after signing).
- Run with network and simulate offline (e.g. an unreachable proxy via `HTTPS_PROXY`) for the revocation question.

## Success criteria
- Working verification code snippets in the report.
- A recommended `TrustPolicy` shape (properties and their exact meaning).
- Clear statement of what works on Linux, what needs the SDK or extra setup, and what can only be verified on Windows.

# 0010. Full trust; optional policy; hashes always pinned

**Status:** Accepted

## Context
Modern .NET has no in-process sandbox. The realistic controls are about *what* gets installed.

## Decision
- Plugins run with full trust. This is documented prominently.
- An optional `TrustPolicy`: allowed feeds, require signed packages, allowed signers/owners.
- Package hashes are always pinned: the `.nupkg` SHA-512 at install, plus each extracted file's size and timestamp. Startup validation checks sizes and timestamps (cheap); `VerifyAsync()` does a full re-hash on demand.

## Consequences
- On-disk tampering is detected.
- Apps needing real isolation must use separate processes; that's out of scope.

## Amendments
- *2026-10-17 (spike: nuget-signature-verification):*
  - Verification uses NuGet's `PackageSignatureVerifier` on the `.nupkg`, before extraction.
  - Policy shape: `AllowedFeeds`, `RequireSignedPackages`, `AllowedSigners` (author fingerprint, or repository fingerprint + owners, each optionally `AllowUntrustedRoot`), and `Revocation` (Online/Offline). See architecture §12.
  - The library **ships the SDK's code-signing and timestamping root bundle** (refreshed with SDK/NuGet updates) and calls the process-wide `X509TrustStore.InitializeForDotNetSdk()`. Without it, Linux rejects most nuget.org packages.
  - Single-file apps would need `NuGet.Packaging.dll` kept on disk (they're unsupported in v1 anyway).
  - Online revocation on a hanging network costs about 120 s per package and can't be cancelled. *Decided 2026-10-18:* the default stays **Online** (NuGet parity, security first). `Offline` is documented prominently for air-gapped and industrial hosts, and verification runs off the UI thread with a visible progress step.
- *2026-10-17 (spike: nuget-resolution-and-assets):* the pinned hash is NuGet's **`contentHash`** (SHA-512 excluding the signature, as in `.nupkg.metadata`), not the whole-file hash. The `.nupkg` is kept for re-verification.
- *2026-10-19:* packages already in `packages/` that a new or changed plan uses (including kept versions for rollback) are re-verified against the current trust policy from their kept `.nupkg`. Acceptances are cached per policy for the host's lifetime, and downloads are always verified.
- *2026-10-21 (Windows run of the signature spike):* verdicts on Windows match Linux's under every policy, with or without the bundle: NuGet uses the Windows trust store there and ignores the fallback bundle, so the bundle stays for Linux and macOS. On Windows, a package whose signature chains to an untrusted root (self-signed authors allowed with `AllowUntrustedRoot`, or accepted with warnings when signatures are optional) takes about 3 s longer to verify, because of NuGet's chain build retries (`NUGET_EXPERIMENTAL_CHAIN_BUILD_RETRY_POLICY`); the verdict is the same. See the [report](../../spike/nuget-signature-verification/REPORT.md#windows-results-2026-10-21).

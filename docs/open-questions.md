# Open questions

Items not yet decided, verification still needed, and deliberately deferred features. When one is resolved, record the outcome (in an ADR if it's significant) and move it to **Resolved** with a one-line summary.

## Needs a decision

Nothing open right now. All design decisions so far are recorded under **Resolved**.

## Verification still needed

### Signature verification on Windows
Run the signature spike's `Verifier matrix` on Windows to confirm identical verdicts with the shipped root bundle ([report](../spike/nuget-signature-verification/REPORT.md)). macOS is untested.

### Native library soname collisions on Windows
*(spike: hot-load-into-loaded-compartment)* On Linux, a native library's own dependencies resolve by soname against everything already loaded in the process, so two cells with different versions of the same native dependency can share one. Check Windows; module-name matching is expected to behave similarly.

## Known limitations to document

### Single-file and trimmed hosts
- **Single-file:** exposes no host inventory (no deps files, empty TPA list). Also, NuGet's trust store lookup crashes unless `NuGet.Packaging.dll` stays on disk. **Unsupported in v1**: `PluginHost.Create` fails with a clear error. A later version could accept a shipped `deps.json`. *(spikes: nuget-resolution-and-assets, nuget-signature-verification)*
- **Trimmed:** trimming can remove types plugins need. Unsupported.

### Package asset types
Only `lib/` and `runtimes/` (plus satellite resource assemblies) are used. `content`, `contentFiles`, `build` and `tools` are ignored. Plugins needing loose files should use embedded resources or their data directory.

### Process-wide effects
NuGet's trust store selection and the opt-in WPF resolving forwarder. (NuGet's user-wide HTTP cache is bypassed.) See [ADR 0012](adr/0012-app-supplies-root-no-global-state.md).

## Resolved

*2026-10-19:*
- **WPF spike on Windows:** run by the user. Every prediction matched except 7.4, which turned out better than predicted (lookless controls' theme styles survive same-identity duplicates). ADR 0015 and the duplicate rule stand. See the [WPF report](../spike/wpf-across-load-contexts/REPORT.md).

*2026-10-18:*
- **Sharing identical private packages across cells:** WPF-relevant packages only ([ADR 0015](adr/0015-share-identical-wpf-assemblies.md), accepted). Other identical packages stay per-cell.
- **Logging abstractions in Abstractions:** accepted. `Microsoft.Extensions.Logging.Abstractions` is host-owned; Abstractions references the lowest supported version; the host's version caps plugins ([ADR 0009](adr/0009-separate-abstractions-package.md)).
- **Revocation default:** `Online`, with `Offline` documented for air-gapped hosts ([ADR 0010](adr/0010-trust-model.md)).
- **Re-activating a Faulted plugin:** no; it loads at next startup and appears in `GetPendingChangesAsync()` ([ADR 0005](adr/0005-hot-install-restart-for-the-rest.md), [ADR 0011](adr/0011-report-failures-never-self-heal.md)).
- **Name:** **Alcove** (a sheltered recess; also a nod to `AssemblyLoadContext`). A compartment is called a **cell**. Package IDs `Alcove`, `Alcove.*` were free on nuget.org; no conflicting .NET projects found. Rejected: Bento (`Bento.SDK`/`Bento.Core` already taken on nuget.org), Cella (kept its "cell" vocabulary instead).

*2026-10-17, decisions:*
- **Extension instance lifetime:** each registration chooses `Singleton` (default) or `Transient`. See architecture §8 and ADR 0007.
- **Configuration precedence:** removed as a question. Whatever options object the app passes in is final; the library never merges sources. See ADR 0013.
- **Plugin-requested vs. app-defined cells:** the app's options win; `isolated` always wins, and conflicts are validation errors. See ADR 0003.
- **Version type in Abstractions:** `NuGetVersion`; Abstractions depends on `NuGet.Versioning`. See ADR 0009.
- **Package hash:** pinned at install, per-file size and timestamp checked at startup, full re-hash through `VerifyAsync()`. See ADR 0010. Refined by the spikes: the pinned hash is NuGet's `contentHash`.

*2026-10-17, spikes:*
- **Cross-cell plugin dependencies:** allowed, with exposed dependencies resolving through the provider's cell ([ADR 0014](adr/0014-cross-cell-dependencies.md), accepted 2026-10-18). The WPF part is covered by the WPF spike.
- **Hot install into a running cell, exact rule:** cells record and freeze their answers. A hot install is OK unless it needs a different answer for a name the cell has already committed (architecture §6, ADR 0005 amendment).
- **Signature verification on Linux:** works when the SDK's root bundle is shipped next to `NuGet.Packaging.dll` and `InitializeForDotNetSdk()` is called (ADR 0010 amendment). Windows confirmation is still listed under verification.
- **NuGet APIs for resolution, assets, download and search:** `RemoteDependencyWalker` + host pins, `ManagedCodeConventions`, `PackageExtractor`, `SearchFilter.PackageType` ([ADR 0016](adr/0016-resolution-with-dependency-walker.md)).

## Deferred (wanted, not v1)

- **Optional plugin dependencies:** "works better with A". Needs a manifest field; NuGet has no equivalent. Keep the manifest schema open for it.
- **`Alcove.Tool`:** `dotnet tool` for managing a plugin root from the command line.
- **`Alcove.Testing`:** fake hosts, in-memory or folder feeds.
- **`Alcove.Wpf.Manager`:** ready-made plugin manager UI.
- **`Alcove.Templates`:** `dotnet new` templates.
- **`IConfiguration` binding add-on.**
- **`TrustPolicy.FromNuGetConfig`:** import NuGet `<trustedSigners>`.
- **Strict revocation** (`Revocation = OnlineStrict`).
- **NuGet plugin credential providers** (e.g. Azure Artifacts' provider). Needs `NuGet.Credentials`; v1 supports `nuget.config` credentials, default network credentials and the app's `ICredentialProvider`.
- **Refresh `AllowedSigner.NuGetOrg`** when nuget.org rotates its repository certificate. The newest one in the list expires 2027-05-18; consider refreshing from the feed's `RepositorySignatures` resource at runtime.
- **Publishing:** nuget.org (and/or GitHub Packages). The repository is on GitHub (github.com/adam8797/alcove).

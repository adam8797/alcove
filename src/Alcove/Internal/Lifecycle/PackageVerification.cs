using System.Security.Cryptography;
using Alcove.Internal.Install;
using Alcove.Internal.IO;
using Alcove.Internal.State;
using Alcove.Internal.Trust;
using Microsoft.Extensions.Logging;
using NuGet.Packaging;

namespace Alcove.Internal.Lifecycle;

/// <summary>
/// <c>VerifyAsync</c> (architecture §12): for every package the state file refers to, recomputes the kept
/// <c>.nupkg</c>'s <c>contentHash</c> and compares it (and <c>.nupkg.metadata</c>'s) with the pinned one, then hashes
/// every extracted file (SHA-512) against the package's own entry, and reports files that are missing or not from the
/// package. Optionally applies the trust policy to each kept <c>.nupkg</c> again. Local only.
/// </summary>
internal sealed class PackageVerification(PackageLayout layout, IPackageVerifier verifier, ILogger logger)
{
    public async Task<VerificationReport> VerifyAsync(StateDocument state, bool verifySignatures, CancellationToken cancellationToken)
    {
        var paths = new VersionFolderPathResolver(layout.PackagesDirectory, isLowercase: true);
        var issues = new List<ValidationIssue>();
        var packages = 0;
        var filesChecked = 0;
        foreach (var pinned in PinnedPackage.All(state))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = pinned.Id;
            var what = $"{id} {pinned.Version}";
            var directory = layout.GetPackageDirectory(id, pinned.Version);
            void Add(ValidationSeverity severity, string code, string message)
            {
                issues.Add(new ValidationIssue(severity, code, message, id));
                LifecycleLog.VerificationIssue(logger, what, message);
            }

            if (!Directory.Exists(directory))
            {
                Add(ValidationSeverity.Error, ValidationCodes.PackageMissing, $"{what} isn't in the plugin root ('{directory}').");
                continue;
            }

            packages++;
            var nupkg = paths.GetPackageFilePath(id, pinned.Version);
            if (!File.Exists(nupkg))
            {
                Add(ValidationSeverity.Error, ValidationCodes.FileMissing, $"The kept package file of {what} ({Path.GetFileName(nupkg)}) is missing, so it can't be re-hashed.");
                continue;
            }

            try
            {
                var metadataHash = PackageRecorder.ReadContentHash(paths.GetNupkgMetadataPath(id, pinned.Version));
                if (pinned.ContentHash is not null && !string.Equals(metadataHash, pinned.ContentHash, StringComparison.Ordinal))
                {
                    Add(ValidationSeverity.Error, ValidationCodes.ContentHashMismatch, $".nupkg.metadata of {what} has the contentHash {metadataHash}, but the state file pins {pinned.ContentHash}.");
                }
            }
            catch (InvalidDataException ex)
            {
                Add(ValidationSeverity.Error, ValidationCodes.ContentHashMismatch, ex.Message);
            }

            var expected = new HashSet<string>(StringComparer.Ordinal)
            {
                Path.GetFileName(nupkg),
                Path.GetFileName(paths.GetNupkgMetadataPath(id, pinned.Version)),
                paths.GetHashFileName(id, pinned.Version),
            };
            using (var reader = new PackageArchiveReader(nupkg))
            {
                var hash = reader.GetContentHash(cancellationToken);
                if (pinned.ContentHash is not null && !string.Equals(hash, pinned.ContentHash, StringComparison.Ordinal))
                {
                    Add(ValidationSeverity.Error, ValidationCodes.ContentHashMismatch, $"The kept package file of {what} has the contentHash {hash}, but the state file pins {pinned.ContentHash}.");
                }

                var changed = new List<string>();
                var missing = new List<string>();
                foreach (var entry in reader.GetFiles().Where(f => !PackageRecorder.IsPackagingFile(f)))
                {
                    var relative = entry.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase) && !entry.Contains('/', StringComparison.Ordinal)
                        ? paths.GetManifestFileName(id, pinned.Version)
                        : entry;
                    expected.Add(relative);
                    var file = Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(file))
                    {
                        missing.Add(relative);
                        continue;
                    }

                    filesChecked++;
                    using var packaged = reader.GetStream(entry);
                    using var extracted = File.OpenRead(file);
                    if (!SHA512.HashData(packaged).AsSpan().SequenceEqual(SHA512.HashData(extracted)))
                    {
                        changed.Add(relative);
                    }
                }

                missing.AddRange(pinned.Files.Select(f => f.Path).Where(p => !expected.Contains(p) && !File.Exists(Path.Combine(directory, p))));
                if (missing.Count > 0)
                {
                    Add(ValidationSeverity.Error, ValidationCodes.FileMissing, $"{Files(missing)} of {what} {(missing.Count == 1 ? "is" : "are")} missing: {string.Join(", ", missing)}.");
                }

                if (changed.Count > 0)
                {
                    Add(ValidationSeverity.Error, ValidationCodes.FileContentChanged, $"{Files(changed)} of {what} differ from the package (SHA-512): {string.Join(", ", changed)}.");
                }
            }

            var unexpected = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(directory, f).Replace('\\', '/'))
                .Where(f => !expected.Contains(f))
                .Order(StringComparer.Ordinal)
                .ToList();
            if (unexpected.Count > 0)
            {
                Add(ValidationSeverity.Warning, ValidationCodes.UnexpectedFile, $"{Files(unexpected)} in the folder of {what} {(unexpected.Count == 1 ? "isn't" : "aren't")} from the package: {string.Join(", ", unexpected)}.");
            }

            if (verifySignatures)
            {
                var decision = await verifier.VerifyPackageAsync(nupkg, id, pinned.Version, cancellationToken).ConfigureAwait(false);
                foreach (var issue in decision.Issues.Where(i => i.Severity != TrustIssueSeverity.Info))
                {
                    Add(issue.Severity == TrustIssueSeverity.Error ? ValidationSeverity.Error : ValidationSeverity.Warning, issue.Code, $"{what}: {issue.Message}");
                }
            }
        }

        return new VerificationReport(issues, packages, filesChecked);

        static string Files(List<string> paths) => paths.Count == 1 ? "1 file" : $"{paths.Count} files";
    }
}

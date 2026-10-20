using Alcove.Install;
using Alcove.Validation;
using NuGet.Versioning;

namespace Alcove.Lifecycle;

/// <summary>What <see cref="PluginHost.RestoreAsync(IProgress{InstallProgress}?, CancellationToken)"/> did. Expected failures are reported here, not thrown.</summary>
public sealed class RestoreResult
{
    internal RestoreResult(IReadOnlyList<RestoredPackage> packages, IReadOnlyList<ValidationIssue> issues)
    {
        Packages = packages;
        Issues = issues;
    }

    /// <summary>Every package the state file pins that was missing from the plugin root, with what happened to it.</summary>
    public IReadOnlyList<RestoredPackage> Packages { get; }

    /// <summary>What the feeds and the trust policy reported (with the trust policy's codes).</summary>
    public IReadOnlyList<ValidationIssue> Issues { get; }

    /// <summary>Whether every missing package was restored.</summary>
    public bool Succeeded => Packages.All(p => p.Restored);

    /// <inheritdoc />
    public override string ToString() =>
        Packages.Count == 0 ? "Nothing was missing." : string.Join(Environment.NewLine, Packages);
}

/// <summary>A package <see cref="PluginHost.RestoreAsync(IProgress{InstallProgress}?, CancellationToken)"/> looked for.</summary>
/// <param name="PackageId">The package ID.</param>
/// <param name="Version">The exact version the state file pins.</param>
/// <param name="Restored">Whether it was downloaded, verified and extracted.</param>
/// <param name="Reason">Why not, if it wasn't (not found, trust policy, <c>contentHash</c> mismatch).</param>
public sealed record RestoredPackage(string PackageId, NuGetVersion Version, bool Restored, string? Reason = null)
{
    /// <inheritdoc />
    public override string ToString() => $"{PackageId} {Version}: {(Restored ? "restored" : $"not restored ({Reason})")}";
}

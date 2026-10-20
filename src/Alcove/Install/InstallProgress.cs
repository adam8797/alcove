using NuGet.Versioning;

namespace Alcove.Install;

/// <summary>A step of <see cref="PluginHost.InstallAsync(string, NuGet.Versioning.VersionRange?, bool?, IProgress{InstallProgress}?, CancellationToken)"/>.</summary>
public enum InstallStep
{
    /// <summary>Resolving the plugin and its dependencies against the feeds and the host.</summary>
    Resolving,

    /// <summary>Downloading a package.</summary>
    Downloading,

    /// <summary>Checking a downloaded package against the trust policy (signatures).</summary>
    VerifyingSignatures,

    /// <summary>Extracting a package into the plugin root.</summary>
    Extracting,

    /// <summary>Recording the installed packages in the state file (hashes, files, assemblies).</summary>
    Recording,

    /// <summary>Loading the installed plugins into the running process.</summary>
    Loading,
}

/// <summary>Progress of <see cref="PluginHost.InstallAsync(string, NuGet.Versioning.VersionRange?, bool?, IProgress{InstallProgress}?, CancellationToken)"/>.</summary>
/// <param name="Step">The current step.</param>
/// <param name="PackageId">The package concerned, if the step is about one package.</param>
/// <param name="Version">That package's version.</param>
public sealed record InstallProgress(InstallStep Step, string? PackageId = null, NuGetVersion? Version = null);

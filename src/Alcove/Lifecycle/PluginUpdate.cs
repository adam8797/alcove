using NuGet.Versioning;

namespace Alcove.Lifecycle;

/// <summary>What <see cref="PluginHost.CheckForUpdatesAsync"/> found for one installed plugin.</summary>
/// <param name="PluginId">The plugin.</param>
/// <param name="InstalledVersion">The version from the next startup on (a staged update's or rollback's target, otherwise the active version).</param>
/// <param name="AvailableVersion">
/// The newest version on the feeds this host can run and the other plugins' version ranges on this one accept, if it's
/// newer than <paramref name="InstalledVersion"/>; otherwise <see langword="null"/>.
/// </param>
/// <param name="IncompatibleVersions">Newer versions than <paramref name="AvailableVersion"/> (or the installed one) that can't be used, newest first, each with the reason.</param>
public sealed record PluginUpdate(
    string PluginId,
    NuGetVersion InstalledVersion,
    NuGetVersion? AvailableVersion,
    IReadOnlyList<IncompatibleVersion> IncompatibleVersions)
{
    /// <summary>Whether <see cref="AvailableVersion"/> is set.</summary>
    public bool IsUpdateAvailable => AvailableVersion is not null;

    /// <inheritdoc />
    public override string ToString() =>
        $"{PluginId} {InstalledVersion}: {(AvailableVersion is null ? "up to date" : $"{AvailableVersion} available")}"
        + (IncompatibleVersions.Count == 0 ? "" : $"; can't use {string.Join(", ", IncompatibleVersions.Select(v => v.Version))}");
}

/// <summary>A newer version of a plugin that can't be used, and why.</summary>
/// <param name="Version">The version.</param>
/// <param name="Reason">Why (e.g. it needs a newer version of the host's contracts, or another plugin's range excludes it).</param>
public sealed record IncompatibleVersion(NuGetVersion Version, string Reason);

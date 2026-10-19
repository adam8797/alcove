using Alcove.Internal.Hosting;
using NuGet.Frameworks;
using NuGet.Packaging;

namespace Alcove.Internal.Search;

/// <summary>
/// The cheap compatibility check of search results: a package's declared dependencies (the group for the host's
/// framework) against the host inventory. Host-owned packages (e.g. the contracts) must be in the host at a version
/// the declared range accepts. Resolution at install is the full check.
/// </summary>
internal static class HostCompatibility
{
    /// <summary>Why the package can't run in this host, or <see langword="null"/> if it looks compatible.</summary>
    public static string? Check(HostInventory host, IReadOnlyList<PackageDependencyGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(groups);
        if (groups.Count == 0)
        {
            return null;
        }

        var group = NuGetFrameworkUtility.GetNearest(groups, host.Framework, g => g.TargetFramework);
        if (group is null)
        {
            return $"It doesn't support this host's framework {host.Framework.GetShortFolderName()} (it targets {string.Join(", ", groups.Select(g => g.TargetFramework.GetShortFolderName()))}).";
        }

        var reasons = new List<string>();
        foreach (var dependency in group.Packages)
        {
            var minimum = dependency.VersionRange.MinVersion;
            if (!host.IsHostOwnedPackage(dependency.Id, minimum) || host.IsFrameworkProvided(dependency.Id, minimum))
            {
                continue;
            }

            var hostVersion = host.FindPackage(dependency.Id)?.Version;
            if (hostVersion is null)
            {
                reasons.Add($"It needs the host-owned package {dependency.Id} {dependency.VersionRange.PrettyPrint()}, which this host doesn't have.");
            }
            else if (!dependency.VersionRange.Satisfies(hostVersion))
            {
                reasons.Add($"It needs the host-owned package {dependency.Id} {dependency.VersionRange.PrettyPrint()}, but this host has {hostVersion}.");
            }
        }

        return reasons.Count == 0 ? null : string.Join(" ", reasons);
    }
}

using System.Collections.Immutable;
using NuGet.Versioning;

namespace Alcove.Internal.Metadata;

/// <summary>One assembly file that a plan loads into one load context.</summary>
/// <param name="Cell">The cell's name, or <see langword="null"/> for the default context (host-owned and host-shared assemblies).</param>
/// <param name="Assembly">The file's WPF metadata (<see cref="WpfAssemblyInspector"/>).</param>
/// <param name="PackageId">The package the file comes from, or <see langword="null"/> if unknown (e.g. the host's own assemblies).</param>
/// <param name="PackageVersion">That package's version, or <see langword="null"/> if unknown.</param>
internal sealed record PlannedAssembly(string? Cell, WpfAssemblyInfo Assembly, string? PackageId, NuGetVersion? PackageVersion)
{
    /// <summary>Whether this file loads into the default context.</summary>
    public bool IsDefaultContext => Cell is null;

    /// <summary>Whether both files come from the same package ID and version.</summary>
    public bool IsSamePackage(PlannedAssembly other) =>
        PackageId is not null && PackageVersion is not null
        && string.Equals(PackageId, other.PackageId, StringComparison.OrdinalIgnoreCase)
        && PackageVersion.Equals(other.PackageVersion);

    public override string ToString() =>
        $"{Assembly.Identity} in {Cell ?? "the default context"} from {(PackageId is null ? "no package" : $"{PackageId} {PackageVersion}")} ({Assembly.Path})";
}

/// <summary>What a WPF duplicate finding means for the plan.</summary>
internal enum WpfDuplicateKind
{
    /// <summary>Identical WPF-relevant private assemblies: load once and share across these cells (ADR 0015). Not a warning.</summary>
    ShareAcrossCells,

    /// <summary>"Will break": WPF resolves every copy to the most recently loaded one. At hot install, <c>PendingRestart</c>.</summary>
    WillBreak,

    /// <summary>"May break": name-only lookups (unversioned pack URIs, loose XAML) go to the most recently loaded copy.</summary>
    MayBreak,
}

/// <summary>Why a finding has its kind.</summary>
internal enum WpfDuplicateReason
{
    /// <summary>Same identity, same package ID and version (<see cref="WpfDuplicateKind.ShareAcrossCells"/>).</summary>
    IdenticalPackage,

    /// <summary>Same identity (name, <c>AssemblyVersion</c>, public key token) from different packages or load contexts.</summary>
    SameIdentity,

    /// <summary>
    /// Same name and <c>AssemblyVersion</c> but a different public key token. <c>InitializeComponent</c> locators name
    /// the version but never the token, so this breaks like <see cref="SameIdentity"/>.
    /// </summary>
    SameAssemblyVersion,

    /// <summary>Different <c>AssemblyVersion</c>s, and a copy has unversioned locators, which resolve to the wrong copy.</summary>
    UnversionedLocators,

    /// <summary>Different <c>AssemblyVersion</c>s.</summary>
    DifferentAssemblyVersion,
}

/// <summary>A WPF-relevant assembly name loaded in more than one load context.</summary>
/// <param name="Kind">What it means.</param>
/// <param name="Reason">Why.</param>
/// <param name="AssemblyName">The simple name.</param>
/// <param name="Copies">The copies concerned: cells, identities (versions), packages and paths.</param>
internal sealed record WpfDuplicateFinding(WpfDuplicateKind Kind, WpfDuplicateReason Reason, string AssemblyName, ImmutableArray<PlannedAssembly> Copies)
{
    /// <summary>The cells concerned (<see langword="null"/> = default context).</summary>
    public IEnumerable<string?> Cells => Copies.Select(c => c.Cell);

    public override string ToString() => $"{Kind} ({Reason}) {AssemblyName}: {string.Join("; ", Copies)}";
}

/// <summary>
/// Applies the WPF duplicate-assembly rule (spike: wpf-across-load-contexts §6; architecture §4) to a load plan.
/// A pure function of its input, for validation and the hot-install check.
/// </summary>
internal static class WpfDuplicateDetector
{
    /// <summary>
    /// Finds every WPF-relevant simple name planned into two or more load contexts.
    /// </summary>
    /// <param name="plan">
    /// Every assembly each load context will hold (the default context included, as far as it matters), not just the
    /// duplicates: WPF relevance depends on the other assemblies in the same context. Each name may appear at most
    /// once per load context.
    /// </param>
    /// <returns>
    /// Per name: a <see cref="WpfDuplicateKind.ShareAcrossCells"/> finding for each group of identical cell-private
    /// copies (same identity, package ID and version), then at most one <see cref="WpfDuplicateKind.WillBreak"/> or
    /// <see cref="WpfDuplicateKind.MayBreak"/> finding if distinct copies remain (a shared group counts as one copy).
    /// Names that aren't WPF-relevant give no finding. Ordered by name.
    /// </returns>
    /// <exception cref="ArgumentException">A name appears twice in one load context.</exception>
    public static ImmutableArray<WpfDuplicateFinding> Detect(IEnumerable<PlannedAssembly> plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var entries = plan.ToList();

        var relevant = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var context in entries.GroupBy(e => e.Cell, StringComparer.Ordinal))
        {
            var duplicate = context.GroupBy(e => e.Assembly.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
            if (duplicate is not null)
            {
                throw new ArgumentException(
                    $"'{duplicate.Key}' is planned more than once into {context.Key ?? "the default context"}.", nameof(plan));
            }

            relevant.UnionWith(WpfRelevance.FindRelevant(context.Select(e => e.Assembly)));
        }

        var findings = ImmutableArray.CreateBuilder<WpfDuplicateFinding>();
        foreach (var byName in entries
            .GroupBy(e => e.Assembly.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1 && relevant.Contains(g.Key))
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var copies = byName.OrderBy(e => e.Cell is not null).ThenBy(e => e.Cell, StringComparer.Ordinal).ToList();

            // Each "instance" is what actually gets loaded: a shared group loads once.
            var instances = new List<List<PlannedAssembly>>();
            foreach (var copy in copies)
            {
                var group = copy.IsDefaultContext
                    ? null
                    : instances.FirstOrDefault(g => !g[0].IsDefaultContext && g[0].Assembly.Identity.Equals(copy.Assembly.Identity) && g[0].IsSamePackage(copy));
                if (group is null)
                {
                    instances.Add([copy]);
                }
                else
                {
                    group.Add(copy);
                }
            }

            foreach (var shared in instances.Where(g => g.Count > 1))
            {
                findings.Add(new WpfDuplicateFinding(WpfDuplicateKind.ShareAcrossCells, WpfDuplicateReason.IdenticalPackage, byName.Key, [.. shared]));
            }

            if (instances.Count > 1)
            {
                var (kind, reason) = Classify(instances.Select(g => g[0]).ToList());
                findings.Add(new WpfDuplicateFinding(kind, reason, byName.Key, [.. copies]));
            }
        }

        return findings.ToImmutable();
    }

    /// <summary>Classifies distinct loaded copies of one name (at least two).</summary>
    private static (WpfDuplicateKind, WpfDuplicateReason) Classify(List<PlannedAssembly> instances)
    {
        var identities = instances.Select(i => i.Assembly.Identity).ToList();
        if (identities.Count != identities.Distinct().Count())
        {
            return (WpfDuplicateKind.WillBreak, WpfDuplicateReason.SameIdentity);
        }

        if (identities.Count != identities.Select(i => i.Version).Distinct().Count())
        {
            return (WpfDuplicateKind.WillBreak, WpfDuplicateReason.SameAssemblyVersion);
        }

        return instances.Any(i => i.Assembly.HasUnversionedLocators)
            ? (WpfDuplicateKind.WillBreak, WpfDuplicateReason.UnversionedLocators)
            : (WpfDuplicateKind.MayBreak, WpfDuplicateReason.DifferentAssemblyVersion);
    }
}

using System.Collections.Immutable;

namespace Alcove.Inspection;

/// <summary>What the WPF duplicate rules need to know about one assembly file (from metadata only).</summary>
/// <param name="Path">The inspected file.</param>
/// <param name="Identity">Simple name, <c>AssemblyVersion</c> and public key token.</param>
/// <param name="HasWpfResources">Whether it contains the manifest resource <c>&lt;SimpleName&gt;.g.resources</c> (compiled XAML).</param>
/// <param name="AssemblyReferences">Its AssemblyRef table.</param>
/// <param name="BamlAssemblyReferences">
/// Assemblies named by the BAML in its <c>.g.resources</c>. XAML can use an assembly the code never does, and the
/// compiler then leaves it out of the AssemblyRef table, but WPF still resolves it by name.
/// </param>
/// <param name="UnversionedLocators">
/// User strings of the form <c>/&lt;SimpleName&gt;;component/...</c> without a <c>;V&lt;version&gt;</c> part: the
/// <c>InitializeComponent</c> locators of an assembly built without an explicit <c>AssemblyVersion</c>, or pack URIs
/// the code builds. WPF resolves these by name only. Empty unless <see cref="HasWpfResources"/>.
/// </param>
internal sealed record WpfAssemblyInfo(
    string Path,
    AssemblyIdentity Identity,
    bool HasWpfResources,
    ImmutableArray<AssemblyIdentity> AssemblyReferences,
    ImmutableArray<AssemblyIdentity> BamlAssemblyReferences,
    ImmutableArray<string> UnversionedLocators)
{
    /// <summary>The simple name.</summary>
    public string Name => Identity.Name;

    /// <summary>Whether WPF can resolve some of this assembly's XAML to whatever copy of it was loaded last.</summary>
    public bool HasUnversionedLocators => !UnversionedLocators.IsEmpty;

    /// <summary>Simple names this assembly references, from the AssemblyRef table and its BAML.</summary>
    public IEnumerable<string> ReferencedNames =>
        AssemblyReferences.Select(r => r.Name).Concat(BamlAssemblyReferences.Select(r => r.Name));
}

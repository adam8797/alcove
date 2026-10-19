using System.Collections.Immutable;

namespace Alcove.Internal.Metadata;

/// <summary>How a dependency's type appears in a plugin's public surface.</summary>
internal enum ExposureKind
{
    BaseType,
    Interface,
    GenericConstraint,
    Field,
    Method,
    Property,
    Event,
    CustomAttribute,
    TypeForwarder,
}

/// <summary>One place in a plugin's public surface that mentions a dependency's type.</summary>
/// <param name="Assembly">File name of the scanned plugin assembly.</param>
/// <param name="Member">The public type or member, e.g. <c>Ns.Type.Method</c>.</param>
/// <param name="Kind">How the type appears there.</param>
internal sealed record ExposureSite(string Assembly, string Member, ExposureKind Kind)
{
    public override string ToString() => $"{Member} ({Kind}, {Assembly})";
}

/// <summary>An <b>exposed dependency</b>: an assembly whose types appear in a plugin's public surface.</summary>
/// <param name="AssemblyName">Simple name of the dependency.</param>
/// <param name="ReferencedVersion">
/// Highest version the plugin's assemblies reference it with, or <see langword="null"/> if it's only named
/// in a custom attribute argument without a version.
/// </param>
/// <param name="Sites">Where it appears (for diagnostics).</param>
internal sealed record ExposedDependency(string AssemblyName, Version? ReferencedVersion, ImmutableArray<ExposureSite> Sites);

/// <summary>A file that was passed to the scanner but holds no .NET metadata (e.g. a native library).</summary>
internal sealed record SkippedFile(string Path, string Reason);

/// <summary>The result of <see cref="ExposedDependencyScanner.Scan"/>.</summary>
/// <param name="Dependencies">Exposed dependencies, ordered by name.</param>
/// <param name="Skipped">Inputs that weren't .NET assemblies.</param>
internal sealed record ExposedDependencyScanResult(ImmutableArray<ExposedDependency> Dependencies, ImmutableArray<SkippedFile> Skipped)
{
    /// <summary>The exposed dependencies' simple names (case-insensitive).</summary>
    public ImmutableHashSet<string> AssemblyNames { get; } =
        Dependencies.Select(d => d.AssemblyName).ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
}

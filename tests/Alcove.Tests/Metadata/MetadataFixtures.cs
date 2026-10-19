using System.Reflection;

namespace Alcove.Tests.Metadata;

/// <summary>
/// Paths of the fixture assemblies under tests/fixtures/Metadata, built by this project's ProjectReferences.
/// They're only read as files, never loaded.
/// </summary>
internal static class MetadataFixtures
{
    private static readonly string RepoRoot =
        typeof(MetadataFixtures).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "AlcoveRepoRoot").Value!;

    public static string LeakSurface => Path("Fixture.Leak.Surface");

    public static string WpfCode => Path("Fixture.Wpf.Code");

    /// <summary>Fixture.Wpf.Controls, AssemblyVersion 1.0.0.0 (explicit).</summary>
    public static string WpfControlsV1 => Path("Fixture.Wpf.Controls.V1", "Fixture.Wpf.Controls");

    /// <summary>Fixture.Wpf.Controls, AssemblyVersion 2.0.0.0 (explicit).</summary>
    public static string WpfControlsV2 => Path("Fixture.Wpf.Controls.V2", "Fixture.Wpf.Controls");

    /// <summary>Fixture.Wpf.Controls, AssemblyVersion 3.0.0.0 inferred from Version: unversioned locators.</summary>
    public static string WpfControlsUnversioned => Path("Fixture.Wpf.Controls.Unversioned", "Fixture.Wpf.Controls");

    /// <summary>A fixture's output assembly.</summary>
    public static string Path(string project, string? assemblyName = null)
    {
        var path = System.IO.Path.Combine(RepoRoot, "tests", "fixtures", "Metadata", "bin", project, $"{assemblyName ?? project}.dll");
        return File.Exists(path) ? path : throw new FileNotFoundException($"Fixture '{path}' wasn't built. Build Alcove.Tests first.", path);
    }

    /// <summary>The test's host-owned predicate: the shared framework.</summary>
    public static bool IsFramework(string name) =>
        name is "System" or "mscorlib" or "netstandard" or "WindowsBase" or "PresentationCore" or "PresentationFramework"
        || name.StartsWith("System.", StringComparison.Ordinal)
        || name.StartsWith("Microsoft.", StringComparison.Ordinal);
}

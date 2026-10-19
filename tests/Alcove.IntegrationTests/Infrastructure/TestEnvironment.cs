using System.Reflection;

namespace Alcove.IntegrationTests.Infrastructure;

/// <summary>Paths and versions of the repository build, baked into the test assembly by MSBuild.</summary>
internal static class TestEnvironment
{
    /// <summary>The repository root.</summary>
    public static string RepoRoot { get; } = Metadata("AlcoveRepoRoot");

    /// <summary>The folder the build packs the Alcove packages into.</summary>
    public static string PackagesDirectory { get; } = Metadata("AlcovePackagesDirectory");

    /// <summary>The version of every Alcove package in this build.</summary>
    public static string AlcoveVersion { get; } = Metadata("AlcoveVersion");

    /// <summary>The fixture projects (tests/fixtures).</summary>
    public static string FixturesDirectory { get; } = Path.Combine(RepoRoot, "tests", "fixtures");

    /// <summary>The path of a packed Alcove package from this build.</summary>
    public static string AlcovePackage(string id)
    {
        var path = Path.Combine(PackagesDirectory, $"{id}.{AlcoveVersion}.nupkg");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"'{path}' wasn't produced by the build. Build the solution first.", path);
        }

        return path;
    }

    private static string Metadata(string key) =>
        typeof(TestEnvironment).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(a => a.Key == key)?.Value
        ?? throw new InvalidOperationException($"Assembly metadata '{key}' is missing.");
}

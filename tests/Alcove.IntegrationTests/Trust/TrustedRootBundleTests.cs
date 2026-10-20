using Alcove.IntegrationTests.Infrastructure;
using Alcove.Trust;
using NuGet.Packaging;

namespace Alcove.IntegrationTests.Trust;

/// <summary>
/// NuGet finds its trusted root bundles only in <c>trustedroots/</c> next to <c>NuGet.Packaging.dll</c>: the Alcove
/// project and package must put them there in every app's output.
/// </summary>
public sealed class TrustedRootBundleTests
{
    private static readonly string[] BundleFiles = ["codesignctl.pem", "timestampctl.pem"];

    [Fact]
    public void ProjectReference_PutsTheBundleNextToNuGetPackaging()
    {
        var nugetDirectory = Path.GetDirectoryName(typeof(PackageArchiveReader).Assembly.Location)!;

        Assert.Equal(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), nugetDirectory);
        foreach (var file in BundleFiles)
        {
            var copied = Path.Combine(nugetDirectory, "trustedroots", file);
            Assert.True(File.Exists(copied), $"'{copied}' is missing.");
            Assert.Equal(File.ReadAllBytes(Path.Combine(TestEnvironment.RepoRoot, "src", "Alcove", "trustedroots", file)), File.ReadAllBytes(copied));
        }

        Assert.Null(PackageVerifier.FindRootBundleProblem());
    }

    [Fact]
    public void Package_ShipsTheBundleWithBuildTransitiveTargets()
    {
        using var package = new PluginPackageReader(TestEnvironment.AlcovePackage("Alcove"));

        Assert.Contains("buildTransitive/Alcove.targets", package.Files);
        foreach (var file in BundleFiles)
        {
            Assert.Contains($"buildTransitive/trustedroots/{file}", package.Files);
        }

        Assert.DoesNotContain(package.Files, f => f.StartsWith("build/", StringComparison.Ordinal) || f.StartsWith("content", StringComparison.Ordinal));
        Assert.Equal(NuGet.Versioning.NuGetVersion.Parse("10.0.13"), package.Dependency("System.Security.Cryptography.Pkcs")?.VersionRange.MinVersion);
    }

    [Fact]
    public async Task PackageReference_CopiesTheBundleNextToNuGetPackaging_OnBuildAndPublish()
    {
        var ct = TestContext.Current.CancellationToken;
        using var workspace = FixtureWorkspace.Create();
        workspace.AddToFeed(TestEnvironment.AlcovePackage("Alcove"));

        // An app that references a library that references Alcove: the bundle must flow transitively.
        var library = Path.Combine(workspace.RootDirectory, "consumer", "Library");
        var app = Path.Combine(workspace.RootDirectory, "consumer", "App");
        Directory.CreateDirectory(library);
        Directory.CreateDirectory(app);
        File.WriteAllText(Path.Combine(library, "Library.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup><PackageReference Include="Alcove" Version="{TestEnvironment.AlcoveVersion}" /></ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(app, "App.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup><ProjectReference Include="../Library/Library.csproj" /></ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(app, "Program.cs"), "System.Console.WriteLine(typeof(Alcove.Trust.TrustPolicy));");

        // The workspace maps "Alcove.*" to its feed; the core package's ID is just "Alcove".
        var nugetConfig = Path.Combine(workspace.RootDirectory, "consumer", "nuget.config");
        File.WriteAllText(nugetConfig, File.ReadAllText(workspace.NuGetConfigPath)
            .Replace("<package pattern=\"Alcove.*\" />", "<package pattern=\"Alcove\" /><package pattern=\"Alcove.*\" />", StringComparison.Ordinal));

        var publish = Path.Combine(workspace.RootDirectory, "publish");
        var result = await workspace.RunAsync(
            ["publish", app, "--output", publish, "--disable-build-servers", $"-p:RestoreConfigFile={nugetConfig}"], ct);
        result.EnsureSuccess();
        Assert.DoesNotContain(": warning ", result.Output, StringComparison.OrdinalIgnoreCase);

        foreach (var output in new[] { Path.Combine(app, "bin", "Release", "net10.0"), publish })
        {
            Assert.True(File.Exists(Path.Combine(output, "NuGet.Packaging.dll")), $"NuGet.Packaging.dll isn't in '{output}'.");
            foreach (var file in BundleFiles)
            {
                Assert.True(File.Exists(Path.Combine(output, "trustedroots", file)), $"trustedroots/{file} isn't in '{output}'.{Environment.NewLine}{result.Output}");
            }
        }
    }
}

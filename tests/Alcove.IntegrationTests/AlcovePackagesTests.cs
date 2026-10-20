using Alcove.IntegrationTests.Infrastructure;
using NuGet.Versioning;

namespace Alcove.IntegrationTests;

/// <summary>The Alcove packages themselves, as the build packs them.</summary>
public sealed class AlcovePackagesTests
{
    [Fact]
    public void Abstractions_DependsOnTheLowestSupportedHostOwnedVersions()
    {
        using var package = new PluginPackageReader(TestEnvironment.AlcovePackage("Alcove.Abstractions"));

        Assert.Equal(["Microsoft.Extensions.Logging.Abstractions", "NuGet.Versioning"],
            package.Dependencies.Select(d => d.Id).Order(StringComparer.Ordinal));
        Assert.Equal(NuGetVersion.Parse("8.0.0"), package.Dependency("Microsoft.Extensions.Logging.Abstractions")!.VersionRange.MinVersion);
        Assert.Contains("lib/net10.0/Alcove.Abstractions.dll", package.Files);
        Assert.Contains("lib/net10.0/Alcove.Abstractions.xml", package.Files);
    }

    [Fact]
    public void Sdk_IsBuildOnly()
    {
        using var package = new PluginPackageReader(TestEnvironment.AlcovePackage("Alcove.Sdk"));

        // Build files plus the nuget.org readme; no assemblies.
        Assert.Equal(["README.md", "build/Alcove.Sdk.props", "build/Alcove.Sdk.targets"], package.Files.Order(StringComparer.Ordinal));
        Assert.Empty(package.Dependencies);
        Assert.True(package.Nuspec.GetDevelopmentDependency());
    }
}

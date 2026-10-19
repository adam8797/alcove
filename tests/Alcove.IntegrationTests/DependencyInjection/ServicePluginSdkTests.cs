using Alcove.IntegrationTests.Infrastructure;

namespace Alcove.IntegrationTests.DependencyInjection;

/// <summary>
/// Alcove.Sdk with the DI add-ons: a plugin referencing them (for <c>IServicePlugin</c>) treats them as host-owned,
/// through the add-ons' buildTransitive props.
/// </summary>
[Collection(DependencyInjectionCollection.Name)]
public sealed class ServicePluginSdkTests(DependencyInjectionFixtures fixtures)
{
    /// <summary>The add-ons, their containers and the core they depend on: provided by the host.</summary>
    private static readonly string[] HostOwnedAssemblies =
    [
        "Alcove.DependencyInjection.dll", "Microsoft.Extensions.DependencyInjection.dll", "Microsoft.Extensions.DependencyInjection.Abstractions.dll",
        "Alcove.Autofac.dll", "Autofac.dll", "Autofac.Extensions.DependencyInjection.dll",
        "Alcove.dll", "Alcove.Abstractions.dll", "Fixture.HostContracts.dll",
    ];

    [Fact]
    public void Pack_KeepsTheAddOnsAsDependencies_AndBundlesOnlyThePlugin()
    {
        using var package = new PluginPackageReader(fixtures.Packages.Single(p => Path.GetFileName(p).StartsWith("Fixture.DI.Alpha.", StringComparison.Ordinal)));

        Assert.NotNull(package.Dependency("Alcove.DependencyInjection"));
        Assert.NotNull(package.Dependency("Alcove.Autofac"));
        Assert.Equal(["lib/net10.0/Fixture.DI.Alpha.dll"], package.Files.Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task Build_DoesNotCopyTheAddOnsOrTheirContainersToTheOutput()
    {
        // EnableDynamicLoading copies package assets to the output, unless they're host-owned.
        var workspace = fixtures.Workspace;
        var clone = workspace.CloneFixture(Path.Combine("DependencyInjection", "Fixture.DI.Alpha"));
        var output = Path.Combine(workspace.RootDirectory, "build-output", Path.GetFileName(clone));

        (await workspace.BuildAsync(clone, [$"OutDir={output}/", "EnableDynamicLoading=true"], TestContext.Current.CancellationToken)).EnsureSuccess();

        var files = Directory.GetFiles(output, "*.dll").Select(Path.GetFileName).ToList();
        Assert.Empty(files.Intersect(HostOwnedAssemblies, StringComparer.OrdinalIgnoreCase));
        Assert.Equal(["Fixture.DI.Alpha.dll"], files);   // nor the core's own dependencies (NuGet.*)
        Assert.False(Directory.Exists(Path.Combine(output, "trustedroots")), "The core's trusted roots were copied to a plugin.");
    }
}

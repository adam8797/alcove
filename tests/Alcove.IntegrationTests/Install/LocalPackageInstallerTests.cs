using System.Security.Cryptography;
using Alcove.Hosting;
using Alcove.IntegrationTests.Infrastructure;
using Alcove.Storage;
using NuGet.Versioning;

namespace Alcove.IntegrationTests.Install;

/// <summary>What the feedless installer (the test helper's core) writes: layout, hashes, file and assembly records, classes.</summary>
public sealed class LocalPackageInstallerTests(HostingFixtures fixtures) : IDisposable
{
    private readonly PluginRootBuilder _root = new();

    public void Dispose() => _root.Dispose();

    [Fact]
    public async Task InstallsIntoTheLowercaseLayout_AndRecordsTheResolvedDependencySet()
    {
        var host = HostInventory.FromCurrentProcess([HostingFixtures.ContractsPackage]);

        var states = await _root.InstallAsync(host, fixtures.Packages, "Fixture.Hosting.Consumer");

        var consumer = states.Single(s => s.Id == "Fixture.Hosting.Consumer");
        Assert.Equal(
            [("Fixture.Dep", "2.0.0", DependencyClass.CellPrivate), ("Fixture.HostContracts", TestEnvironment.AlcoveVersion, DependencyClass.HostOwned), ("Fixture.Hosting.Provider", "1.0.0", DependencyClass.Plugin)],
            consumer.Dependencies.Select(d => (d.Id, d.Version.ToNormalizedString(), d.Classification)));
        Assert.Equal("lib/net10.0/Fixture.Hosting.Consumer.dll", Assert.Single(consumer.Assemblies).Path);
        Assert.Equal("1.0.0.0", consumer.Assemblies[0].AssemblyVersion);
        Assert.Null(consumer.Cell);   // no cell request in the manifest; assignment happens at load
        Assert.Empty(consumer.ExposedDependencies);

        var directory = Path.Combine(_root.Root, "packages", "fixture.hosting.consumer", "1.0.0");
        Assert.True(File.Exists(Path.Combine(directory, "fixture.hosting.consumer.1.0.0.nupkg")));
        Assert.True(File.Exists(Path.Combine(directory, "fixture.hosting.consumer.nuspec")));
        Assert.True(File.Exists(Path.Combine(directory, ".nupkg.metadata")));
        Assert.True(File.Exists(Path.Combine(directory, "Fixture.Hosting.Consumer.plugin.json")));

        // Unsigned package: contentHash is the SHA-512 of the whole file.
        var nupkg = fixtures.Packages.Single(p => Path.GetFileName(p) == "Fixture.Hosting.Consumer.1.0.0.nupkg");
        Assert.Equal(Convert.ToBase64String(SHA512.HashData(File.ReadAllBytes(nupkg))), consumer.ContentHash);
        Assert.Contains(consumer.Files, f => f.Path == "lib/net10.0/Fixture.Hosting.Consumer.dll" && f.Size == new FileInfo(Path.Combine(directory, "lib", "net10.0", "Fixture.Hosting.Consumer.dll")).Length);

        var dep = consumer.Dependencies[0];
        Assert.NotNull(dep.ContentHash);
        Assert.Equal("Fixture.Dep", Assert.Single(dep.Assemblies).Name);
        Assert.NotEmpty(dep.Files);

        var state = new StateStore(_root.Root).Read();
        Assert.Equal(["Fixture.Hosting.Provider", "Fixture.Hosting.Consumer"], state.Plugins.Select(p => p.Id));
        Assert.Equal(["Fixture.Dep"], state.Find("Fixture.Hosting.Provider")!.ExposedDependencies);
    }

    [Fact]
    public async Task InstallingAgain_ReplacesTheStateEntries()
    {
        var host = HostInventory.FromCurrentProcess([HostingFixtures.ContractsPackage]);

        await _root.InstallAsync(host, fixtures.Packages, "Fixture.Hosting.UserA");
        await _root.InstallAsync(host, fixtures.Packages, "Fixture.Hosting.UserA", "Fixture.Hosting.UserB");

        var state = new StateStore(_root.Root).Read();
        Assert.Equal(["Fixture.Hosting.UserA", "Fixture.Hosting.UserB"], state.Plugins.Select(p => p.Id).Order());
        Assert.Equal(NuGetVersion.Parse("1.0.0"), state.Find("Fixture.Hosting.UserA")!.Dependencies.Single(d => d.Id == "Fixture.Dep").Version);
    }

    [Fact]
    public async Task AMissingDependency_IsAClearError()
    {
        var host = HostInventory.FromCurrentProcess([HostingFixtures.ContractsPackage]);
        var withoutDep = fixtures.Packages.Where(p => !Path.GetFileName(p).StartsWith("Fixture.Dep.", StringComparison.Ordinal));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _root.InstallAsync(host, withoutDep, "Fixture.Hosting.UserB"));

        Assert.Contains("Fixture.Dep", ex.Message);
    }
}

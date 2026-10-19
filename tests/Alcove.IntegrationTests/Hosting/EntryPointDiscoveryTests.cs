using Alcove.Internal.Hosting;
using Alcove.Internal.State;
using Alcove.IntegrationTests.Infrastructure;
using Fixture.HostContracts;

namespace Alcove.IntegrationTests.Hosting;

/// <summary>
/// Which entry point a plugin gets: the manifest's (<c>PluginEntryPoint</c>); else its one public, non-abstract
/// <see cref="IPlugin"/> class; several are a validation error; none means <see cref="ExtensionAttribute"/> scanning.
/// </summary>
public sealed class EntryPointDiscoveryTests(HostingFixtures fixtures) : IDisposable
{
    private const string Discovered = "Fixture.Hosting.Discovered";
    private const string Ambiguous = "Fixture.Hosting.Ambiguous";
    private const string Declared = "Fixture.Hosting.Declared";
    private const string UserA = "Fixture.Hosting.UserA";

    private static readonly Lazy<HostInventory> s_inventory = new(() => HostInventory.FromCurrentProcess([HostingFixtures.ContractsPackage]));

    private readonly PluginRootBuilder _root = new();
    private readonly List<PluginHost> _hosts = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (var host in _hosts)
        {
            host.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        _root.Dispose();
    }

    [Fact]
    public async Task WithoutPluginEntryPoint_ThePluginsOneIPluginClassIsTheEntryPoint()
    {
        var states = await _root.InstallAsync(s_inventory.Value, fixtures.Packages, Discovered);
        Assert.Null(Manifest(states.Single()).EntryPoint);
        var host = Create();

        Assert.Empty((await host.ValidateAsync(Ct)).Issues);
        await host.LoadAsync(Ct);

        // Its abstract base class and an internal IPlugin class aren't candidates; its [Extension] type isn't scanned.
        Assert.Equal(PluginStatus.Loaded, Assert.Single(host.Plugins).Status);
        Assert.Equal(["registered by the discovered entry point"], host.Catalog.GetAll<IDescriber>().Select(e => e.Value.Describe()));
    }

    [Fact]
    public async Task WithoutPluginEntryPoint_SeveralIPluginClasses_AreAValidationError()
    {
        await _root.InstallAsync(s_inventory.Value, fixtures.Packages, Ambiguous, UserA);
        var host = Create();

        var error = Assert.Single((await host.ValidateAsync(Ct)).Errors);
        Assert.Equal((ValidationCodes.EntryPointAmbiguous, Ambiguous), (error.Code, error.PluginId));
        Assert.Contains("Fixture.Hosting.Ambiguous.FirstPlugin, Fixture.Hosting.Ambiguous.SecondPlugin", error.Message);
        Assert.Contains("PluginEntryPoint", error.Message);

        await host.LoadAsync(Ct);
        var ambiguous = host.Plugins.Single(p => p.Id == Ambiguous);
        Assert.Equal(PluginStatus.NotLoaded, ambiguous.Status);
        Assert.Equal(error.Message, ambiguous.Reason);
        Assert.Equal(PluginStatus.Loaded, host.Plugins.Single(p => p.Id == UserA).Status);
    }

    [Fact]
    public async Task InstallingAPluginWithSeveralIPluginClassesAndNoEntryPoint_Fails()
    {
        var host = Create(withFeed: true);
        await host.LoadAsync(Ct);

        var result = await host.InstallAsync(Ambiguous, Ct);

        Assert.Equal(InstallStatus.Failed, result.Status);
        Assert.Contains(result.Issues, i => i.Code == ValidationCodes.EntryPointAmbiguous);
        Assert.Empty(new StateStore(_root.Root).Read().Plugins);
    }

    [Fact]
    public async Task TheManifestsEntryPoint_WinsOverOtherIPluginClasses()
    {
        var states = await _root.InstallAsync(s_inventory.Value, fixtures.Packages, Declared);
        Assert.Equal("Fixture.Hosting.Declared.ChosenPlugin", Manifest(states.Single()).EntryPoint);
        var host = Create();

        Assert.Empty((await host.ValidateAsync(Ct)).Issues);
        await host.LoadAsync(Ct);

        Assert.Equal(["chosen"], host.Catalog.GetAll<IDescriber>().Select(e => e.Value.Describe()));
    }

    [Fact]
    public async Task WithoutAnyIPluginClass_ThePluginIsScannedForExtensionAttributes()
    {
        var host = Create(withFeed: true);
        await host.LoadAsync(Ct);

        var result = await host.InstallAsync(Discovered, Ct);
        var attributeOnly = await host.InstallAsync(UserA, Ct);

        Assert.True(result.Status == InstallStatus.Loaded, result.ToString());
        Assert.True(attributeOnly.Status == InstallStatus.Loaded, attributeOnly.ToString());
        Assert.Equal(
            [(Discovered, "registered by the discovered entry point"), (UserA, "UserA uses Dep 1.0 in 'Fixture.Hosting.UserA'")],
            host.Catalog.GetAll<IDescriber>().Select(e => (e.Plugin.Id, e.Value.Describe())));
        Assert.Single(host.Catalog.GetAll<IGreeter>(), e => e.Plugin.Id == UserA);
    }

    private PluginManifest Manifest(PluginState state)
    {
        using var stream = File.OpenRead(_root.Layout.GetManifestPath(state.Id, state.Version));
        return PluginManifest.Read(stream);
    }

    private PluginHost Create(bool withFeed = false)
    {
        var options = new PluginHostOptions
        {
            Root = _root.Root,
            PluginPackageType = "FixturePlugin",
            HostOwnedPackages = { HostingFixtures.ContractsPackage },
        };
        if (withFeed)
        {
            options.Feeds.Add(new PackageFeed(fixtures.PackagesDirectory));
        }

        var host = PluginHost.Create(options);
        _hosts.Add(host);
        return host;
    }
}

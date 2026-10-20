using Alcove.Cells;
using Alcove.Hosting;
using Alcove.Inspection;
using Alcove.Loading;
using Alcove.Packaging;
using Alcove.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using NuGet.Versioning;

namespace Alcove.Tests.Inspection;

/// <summary>
/// Whether a plugin registers services (<see cref="ICellServicesFactory.ServicePluginInterfaces"/>), read from
/// metadata only, against the fixture plugin Fixture.Services.Plugin: its candidate entry points implement
/// Fixture.Services.Contracts.IServicePlugin in different ways, or not at all.
/// </summary>
public sealed class EntryPointInterfaceDetectorTests
{
    private const string Interface = "Fixture.Services.Contracts.IServicePlugin";
    private const string PluginId = "Fixture.Services.Plugin";
    private const string Namespace = "Fixture.Services.Plugin.";

    private static readonly string PluginAssembly = MetadataFixtures.Path("Fixture.Services.Plugin");

    [Fact]
    public void ImplementingTheInterfaceDirectly_RegistersServices() =>
        Assert.True(RegistersServices(Namespace + "DirectPlugin"));

    [Fact]
    public void ThroughABaseClassOfThePluginsAssembly_RegistersServices() =>
        Assert.True(RegistersServices(Namespace + "DerivedPlugin"));

    [Fact]
    public void ThroughAGenericBaseClassOfThePluginsAssembly_RegistersServices() =>
        Assert.True(RegistersServices(Namespace + "GenericDerivedPlugin"));

    [Fact]
    public void ThroughAnInterfaceOfThePluginsAssemblyExtendingIt_RegistersServices() =>
        Assert.True(RegistersServices(Namespace + "ExtendedInterfacePlugin"));

    [Fact]
    public void ANestedEntryPoint_IsFoundByItsReflectionName() =>
        Assert.True(RegistersServices(Namespace + "Outer+NestedPlugin"));

    [Fact]
    public void ThroughABaseClassOfAnotherPrivateAssemblyOfTheCell_RegistersServices() =>
        Assert.True(RegistersServices(Namespace + "LibraryDerivedPlugin", withLibrary: true));

    [Fact]
    public void ThroughABaseClassOfAnAssemblyOutsideTheCell_IsNotFollowed() =>
        Assert.False(RegistersServices(Namespace + "LibraryDerivedPlugin", withLibrary: false));

    [Fact]
    public void ThroughABaseClassOfAHostOwnedAssembly_RegistersServices() =>
        Assert.True(RegistersServices(Namespace + "HostSdkDerivedPlugin", hostFiles: HostSdk(isHostOwned: true)));

    [Fact]
    public void ThroughABaseClassOfAHostAssemblyTheCellPlanListsAsHostShared_RegistersServices()
    {
        var plan = Plan(withLibrary: false);
        plan.HostShared.Add(HostSdkName);

        Assert.True(Providers(HostSdk(isHostOwned: false)).RegistersServices(Planned(Namespace + "HostSdkDerivedPlugin"), plan));
    }

    [Fact]
    public void ABaseClassOfAnAssemblyTheHostDoesntProvide_IsNotFollowed() =>
        Assert.False(RegistersServices(Namespace + "HostSdkDerivedPlugin", hostFiles: HostSdk(isHostOwned: false)));

    [Fact]
    public void HostAssemblyFiles_OfTheCurrentProcess_FindHostAssemblies_ButNotTheSharedFrameworks()
    {
        var files = HostAssemblyFiles.FromCurrentProcess(HostInventory.FromCurrentProcess([]));

        Assert.Equal(typeof(IPlugin).Assembly.Location, files.Find("Alcove.Abstractions"));   // host-owned
        Assert.Null(files.Find("System.Private.CoreLib"));
        Assert.Null(files.Find("System.Runtime", isHost: true));
        Assert.Null(files.Find("No.Such.Assembly", isHost: true));
    }

    [Fact]
    public void AnEntryPointImplementingNeither_RegistersNoServices() =>
        Assert.False(RegistersServices(Namespace + "PlainPlugin"));

    [Fact]
    public void APluginWithoutAnEntryPoint_RegistersNoServices() =>
        Assert.False(RegistersServices(entryPoint: null));

    [Fact]
    public void AnEntryPointThatIsntInThePluginsAssemblies_RegistersNoServices() =>
        Assert.False(RegistersServices(Namespace + "Missing"));

    [Fact]
    public void WithoutAFactory_NothingRegistersServices()
    {
        var providers = new CellServiceProviders(null, new EmptyServices(), NullLogger.Instance);

        Assert.False(providers.RegistersServices(Planned(Namespace + "DirectPlugin"), Plan(withLibrary: true)));
    }

    [Fact]
    public void Detector_MatchesAnyOfTheNames_AndNoneWhenEmpty()
    {
        Assert.True(EntryPointInterfaceDetector.Implements(
            Namespace + "PlainPlugin", [PluginAssembly], ["Some.Other", "System.IDisposable"], _ => null));
        Assert.False(EntryPointInterfaceDetector.Implements(Namespace + "DirectPlugin", [PluginAssembly], [], _ => null));
    }

    private const string HostSdkName = "Fixture.Services.HostSdk";

    private static bool RegistersServices(string? entryPoint, bool withLibrary = true, HostAssemblyFiles? hostFiles = null) =>
        Providers(hostFiles).RegistersServices(Planned(entryPoint), Plan(withLibrary));

    private static CellServiceProviders Providers(HostAssemblyFiles? hostFiles) =>
        new(new Factory(), new EmptyServices(), NullLogger.Instance, hostFiles);

    /// <summary>A host carrying the fixture SDK's file, host-owned or (for the inventory) not provided at all.</summary>
    private static HostAssemblyFiles HostSdk(bool isHostOwned) =>
        new(new Host(isHostOwned ? HostSdkName : null), () => new Dictionary<string, string> { [HostSdkName] = MetadataFixtures.Path(HostSdkName) });

    private static PlannedPlugin Planned(string? entryPoint) => new(
        new PluginState { Id = PluginId, Version = new NuGetVersion(1, 0, 0), ContentHash = "" },
        new PluginManifest { EntryPoint = entryPoint },
        "Cell",
        PluginStatus.NotLoaded,
        null);

    /// <summary>The plugin's cell: its own assembly, and (optionally) the library its package depends on.</summary>
    private static CellPlan Plan(bool withLibrary)
    {
        var plan = new CellPlan();
        plan.Private[PluginId] = new PrivateAssembly(PluginId, PluginAssembly, PluginId, new NuGetVersion(1, 0, 0));
        if (withLibrary)
        {
            const string library = "Fixture.Services.Library";
            plan.Private[library] = new PrivateAssembly(library, MetadataFixtures.Path(library), library, new NuGetVersion(1, 0, 0));
        }

        return plan;
    }

    private sealed class Factory : ICellServicesFactory
    {
        public IReadOnlyCollection<string> ServicePluginInterfaces { get; } = [Interface];

        public IServiceProvider CreateServices(CellServicesContext context) => throw new NotSupportedException();
    }

    private sealed class Host(string? hostOwned) : IHostAssemblies
    {
        public bool IsHostOwnedAssembly(string assemblyName) => assemblyName == hostOwned;

        public bool IsHostSharedAssembly(string assemblyName) => false;
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}

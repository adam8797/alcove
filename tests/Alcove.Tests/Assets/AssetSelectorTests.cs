using Alcove.Internal.Assets;
using NuGet.Frameworks;

namespace Alcove.Tests.Assets;

public sealed class AssetSelectorTests
{
    private static readonly NuGetFramework Net10 = FrameworkConstants.CommonFrameworks.Net10_0;

    private static readonly string[] Package =
    [
        "lib/netstandard2.0/Lib.dll",
        "lib/net8.0/Lib.dll",
        "lib/net8.0/Lib.xml",
        "lib/net8.0/de/Lib.resources.dll",
        "lib/net8.0/fr/Lib.resources.dll",
        "lib/netstandard2.0/de/Lib.resources.dll",
        "lib/net8.0-windows7.0/Lib.dll",
        "runtimes/win/lib/net8.0/Lib.dll",
        "runtimes/linux-x64/native/libnat.so",
        "runtimes/osx/native/libnat.dylib",
        "runtimes/win-x64/native/nat.dll",
        "content/readme.txt",
        "contentFiles/any/any/x.cs",
        "build/Lib.props",
        "tools/install.ps1",
        "Lib.nuspec",
    ];

    [Fact]
    public void ConcurrentSelections_AreSafe()
    {
        // NuGet's conventions cache parsed frameworks in plain dictionaries; one selector is shared by every host.
        var selector = new AssetSelector(AssetSelector.LoadEmbeddedRuntimeGraph());
        var frameworks = new[] { "net10.0", "net9.0", "net8.0", "net10.0-windows7.0", "netstandard2.1" }.Select(NuGetFramework.Parse).ToArray();

        var results = new System.Collections.Concurrent.ConcurrentBag<int>();
        Parallel.For(0, 200, i => results.Add(selector.Select(Package, frameworks[i % frameworks.Length], i % 2 == 0 ? "linux-x64" : "win-x64").RuntimeAssemblies.Count));

        Assert.Equal(200, results.Count);
    }

    [Fact]
    public void PicksTheNearestFrameworkAndItsSatellites()
    {
        var assets = AssetSelector.Default.Select(Package, Net10, runtimeIdentifier: null);

        Assert.Equal(["lib/net8.0/Lib.dll"], assets.RuntimeAssemblies);
        Assert.Equal([new SatelliteAsset("lib/net8.0/de/Lib.resources.dll", "de"), new SatelliteAsset("lib/net8.0/fr/Lib.resources.dll", "fr")], assets.ResourceAssemblies);
        Assert.Empty(assets.NativeLibraries);   // RID-specific: needs a runtime identifier
    }

    [Theory]
    [InlineData("linux-x64", "lib/net8.0/Lib.dll", "runtimes/linux-x64/native/libnat.so")]
    [InlineData("linux-musl-x64", "lib/net8.0/Lib.dll", "runtimes/linux-x64/native/libnat.so")]   // musl imports linux-x64
    [InlineData("win-x64", "runtimes/win/lib/net8.0/Lib.dll", "runtimes/win-x64/native/nat.dll")]
    [InlineData("win-arm64", "runtimes/win/lib/net8.0/Lib.dll", null)]
    [InlineData("osx-arm64", "lib/net8.0/Lib.dll", "runtimes/osx/native/libnat.dylib")]
    public void UsesTheRuntimeIdentifierFallbackGraph(string rid, string runtime, string? native)
    {
        var assets = AssetSelector.Default.Select(Package, Net10, rid);

        Assert.Equal([runtime], assets.RuntimeAssemblies);
        Assert.Equal(native is null ? [] : [native], assets.NativeLibraries);
    }

    [Fact]
    public void TheWindowsPlatformVersionMatters()
    {
        // deps.json says net10.0-windows; the entry assembly says net10.0-windows7.0. Only the latter picks the windows lib.
        var withVersion = AssetSelector.Default.Select(Package, NuGetFramework.Parse("net10.0-windows7.0"), null);
        var withoutVersion = AssetSelector.Default.Select(Package, NuGetFramework.Parse("net10.0-windows"), null);

        Assert.Equal(["lib/net8.0-windows7.0/Lib.dll"], withVersion.RuntimeAssemblies);
        Assert.Equal(["lib/net8.0/Lib.dll"], withoutVersion.RuntimeAssemblies);
    }

    [Fact]
    public void AnEmptyFolderMarkerMeansNothingIsNeeded()
    {
        var assets = AssetSelector.Default.Select(["lib/netcoreapp2.1/_._", "lib/netstandard2.0/System.Threading.Tasks.Extensions.dll"], Net10, "linux-x64");

        Assert.Empty(assets.RuntimeAssemblies);
    }

    [Fact]
    public void IncompatibleOrMissingAssetsSelectNothing()
    {
        Assert.Empty(AssetSelector.Default.Select(["lib/net48/Old.dll"], Net10, null).RuntimeAssemblies);
        Assert.Same(PackageAssets.None, AssetSelector.Default.Select(["content/a.txt", "build/a.props"], Net10, null));
        Assert.Same(PackageAssets.None, AssetSelector.Default.SelectFromDirectory(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), Net10, null));
    }

    [Fact]
    public void TheEmbeddedRidGraphIsTheSdks()
    {
        var graph = AssetSelector.Default.RuntimeGraph;

        Assert.Contains("linux-x64", graph.Runtimes.Keys);
        Assert.Contains("unix", graph.ExpandRuntime("osx-arm64"));
        Assert.Contains("win", graph.ExpandRuntime("win-x64"));
    }
}

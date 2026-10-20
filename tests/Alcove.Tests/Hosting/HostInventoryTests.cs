using System.Reflection;
using System.Runtime.Loader;
using Alcove.Hosting;
using Alcove.Packaging;
using Alcove.Tests.TestSupport;
using NuGet.Frameworks;
using NuGet.Versioning;

namespace Alcove.Tests.Hosting;

public sealed class HostInventoryTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    /// <summary>A host app's deps.json: packages, a project, dependencies, and a package version that differs from its AssemblyVersion.</summary>
    internal const string AppDepsJson = """
        {
          "runtimeTarget": { "name": ".NETCoreApp,Version=v10.0", "signature": "" },
          "targets": {
            ".NETCoreApp,Version=v10.0": {
              "MyApp/1.0.0": {
                "dependencies": { "Alcove.Abstractions": "0.1.0", "MyApp.Contracts": "2.0.0", "Serilog": "3.1.1" },
                "runtime": { "MyApp.dll": {} }
              },
              "Alcove.Abstractions/0.1.0": {
                "dependencies": { "Microsoft.Extensions.Logging.Abstractions": "8.0.0", "NuGet.Versioning": "7.9.0" },
                "runtime": { "lib/net10.0/Alcove.Abstractions.dll": { "assemblyVersion": "0.1.0.0", "fileVersion": "0.1.0.0" } }
              },
              "Microsoft.Extensions.Logging.Abstractions/8.0.0": {
                "dependencies": { "Microsoft.Extensions.DependencyInjection.Abstractions": "8.0.0" },
                "runtime": { "lib/net8.0/Microsoft.Extensions.Logging.Abstractions.dll": { "assemblyVersion": "8.0.0.0", "fileVersion": "8.0.23.53103" } }
              },
              "Microsoft.Extensions.DependencyInjection.Abstractions/8.0.0": {
                "runtime": { "lib/net8.0/Microsoft.Extensions.DependencyInjection.Abstractions.dll": { "assemblyVersion": "8.0.0.0", "fileVersion": "8.0.23.53103" } }
              },
              "NuGet.Versioning/7.9.0": {
                "runtime": { "lib/net8.0/NuGet.Versioning.dll": { "assemblyVersion": "7.9.0.0", "fileVersion": "7.9.0.83" } }
              },
              "MyApp.Contracts/2.0.0": {
                "dependencies": { "MyApp.Primitives": "2.0.0" },
                "runtime": { "lib/net10.0/MyApp.Contracts.dll": { "assemblyVersion": "2.0.0.0" } }
              },
              "MyApp.Primitives/2.0.0": {
                "runtime": { "lib/net10.0/MyApp.Primitives.dll": { "assemblyVersion": "2.0.0.0" } }
              },
              "Serilog/3.1.1": {
                "runtime": { "lib/net7.0/Serilog.dll": { "assemblyVersion": "2.0.0.0", "fileVersion": "3.1.1.0" } }
              }
            }
          },
          "libraries": {
            "MyApp/1.0.0": { "type": "project", "serviceable": false, "sha512": "" },
            "Alcove.Abstractions/0.1.0": { "type": "package", "serviceable": true, "sha512": "sha512-abstractions" },
            "Microsoft.Extensions.Logging.Abstractions/8.0.0": { "type": "package", "serviceable": true, "sha512": "sha512-mela" },
            "Microsoft.Extensions.DependencyInjection.Abstractions/8.0.0": { "type": "package", "serviceable": true, "sha512": "sha512-media" },
            "NuGet.Versioning/7.9.0": { "type": "package", "serviceable": true, "sha512": "sha512-versioning" },
            "MyApp.Contracts/2.0.0": { "type": "package", "serviceable": true, "sha512": "sha512-contracts" },
            "MyApp.Primitives/2.0.0": { "type": "package", "serviceable": true, "sha512": "sha512-primitives" },
            "Serilog/3.1.1": { "type": "package", "serviceable": true, "sha512": "sha512-serilog" }
          }
        }
        """;

    internal const string FrameworkDepsJson = """
        {
          "runtimeTarget": { "name": ".NETCoreApp,Version=v10.0/linux-x64", "signature": "" },
          "targets": {
            ".NETCoreApp,Version=v10.0/linux-x64": {
              "Microsoft.NETCore.App.Runtime.linux-x64/10.0.7": {
                "runtime": {
                  "System.Runtime.dll": { "assemblyVersion": "10.0.0.0", "fileVersion": "10.0.726.21808" },
                  "System.Text.Json.dll": { "assemblyVersion": "10.0.0.0", "fileVersion": "10.0.726.21808" },
                  "System.Collections.dll": { "assemblyVersion": "10.0.0.0", "fileVersion": "10.0.726.21808" }
                }
              }
            }
          },
          "libraries": { "Microsoft.NETCore.App.Runtime.linux-x64/10.0.7": { "type": "package", "serviceable": true, "sha512": "" } }
        }
        """;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void ReadsPackagesProjectsAndAssemblies()
    {
        var inventory = Create();

        Assert.Equal(NuGetVersion.Parse("3.1.1"), inventory.FindPackage("serilog")!.Version);
        Assert.True(inventory.FindPackage("Serilog")!.IsPackage);
        Assert.Equal("sha512-serilog", inventory.FindPackage("Serilog")!.Sha512);
        Assert.False(inventory.FindPackage("MyApp")!.IsPackage);

        // Package version and AssemblyVersion differ (Serilog 3.1.1 has AssemblyVersion 2.0.0.0): both are kept.
        var serilog = inventory.Assemblies["Serilog"];
        Assert.Equal(("Serilog", new Version(2, 0, 0, 0)), (serilog.Owner, serilog.AssemblyVersion));
        Assert.Equal("MyApp", inventory.Assemblies["MyApp"].Owner);
        Assert.Equal(["MyApp.Primitives"], inventory.FindPackage("MyApp.Contracts")!.Dependencies);
    }

    [Fact]
    public void ReadsSharedFrameworksFromTheirPaths()
    {
        var inventory = Create();

        var framework = Assert.Single(inventory.SharedFrameworks);
        Assert.Equal(("Microsoft.NETCore.App", NuGetVersion.Parse("10.0.7")), (framework.Name, framework.Version));
        Assert.Equal("Microsoft.NETCore.App", inventory.FrameworkAssemblies["System.Runtime"].Owner);
        Assert.False(inventory.Assemblies.ContainsKey("System.Runtime"));
    }

    [Fact]
    public void HostOwned_IsClosedOverTheDependencyGraph()
    {
        var inventory = Create(hostOwned: ["MyApp.Contracts"]);

        Assert.Equal(
            ["Alcove.Abstractions", "Microsoft.Extensions.DependencyInjection.Abstractions", "Microsoft.Extensions.Logging.Abstractions", "MyApp.Contracts", "MyApp.Primitives", "NuGet.Versioning"],
            inventory.HostOwnedPackages.Order(StringComparer.Ordinal));
        Assert.True(inventory.IsHostOwnedAssembly("MyApp.Primitives"));
        Assert.True(inventory.IsHostOwnedAssembly("Microsoft.Extensions.DependencyInjection.Abstractions"));
        Assert.True(inventory.IsHostOwnedAssembly("System.Runtime"));
        Assert.False(inventory.IsHostOwnedAssembly("Serilog"));
        Assert.True(inventory.IsHostSharedAssembly("Serilog"));
        Assert.False(inventory.IsHostSharedAssembly("MyApp.Contracts"));
        Assert.False(inventory.IsHostSharedAssembly("Unknown.Library"));
    }

    [Fact]
    public void HostOwned_WithoutTheAppListingItsContracts_LeavesThemHostShared()
    {
        var inventory = Create();

        Assert.False(inventory.IsHostOwnedPackage("MyApp.Contracts"));
        Assert.True(inventory.IsHostSharedAssembly("MyApp.Contracts"));
    }

    [Fact]
    public void HostOwned_APackageTheHostDoesNotCarry_CountsByItsId()
    {
        var inventory = Create(hostOwned: ["MyApp.Sdk"]);

        Assert.True(inventory.IsHostOwnedPackage("MyApp.Sdk"));
        Assert.True(inventory.IsHostOwnedAssembly("MyApp.Sdk"));
    }

    [Theory]
    [InlineData("System.Text.Json", "9.0.0", true)]
    [InlineData("System.Text.Json", "10.0.3", true)]
    [InlineData("System.Text.Json", "11.0.0", false)]
    [InlineData("System.Text.Json", null, true)]
    [InlineData("runtime.unix.System.Collections", "4.3.0", true)]
    [InlineData("Serilog", "3.1.1", false)]
    public void FrameworkProvided_FollowsTheSdkPruningRule(string id, string? version, bool expected)
    {
        var inventory = Create();

        Assert.Equal(expected, inventory.IsFrameworkProvided(id, version is null ? null : NuGetVersion.Parse(version)));
        Assert.Equal(expected, inventory.IsHostOwnedPackage(id, version is null ? null : NuGetVersion.Parse(version)));
    }

    [Fact]
    public void Framework_ComesFromTheEntryAssemblyAttributes()
    {
        Assert.Equal("net10.0", Create(entry: typeof(HostInventoryTests).Assembly).Framework.GetShortFolderName());
        Assert.Equal("net10.0-windows7.0", HostInventory.FrameworkFromAttributes(".NETCoreApp,Version=v10.0", "Windows7.0").GetShortFolderName());
        Assert.Equal("net10.0-windows10.0.19041", HostInventory.FrameworkFromAttributes(".NETCoreApp,Version=v10.0", "Windows10.0.19041.0").GetShortFolderName());
        Assert.Equal("net10.0", HostInventory.FrameworkFromAttributes(".NETCoreApp,Version=v10.0", null).GetShortFolderName());
    }

    [Fact]
    public void Framework_WithoutAnEntryAssembly_FallsBackToDepsJson()
    {
        Assert.Equal(FrameworkConstants.CommonFrameworks.Net10_0, Create(entry: null).Framework);
    }

    [Fact]
    public void SelfContainedHost_ReadsTheFrameworkFromTheRuntimePack()
    {
        var appDeps = _temp.WriteFile("selfcontained/app.deps.json", """
            {
              "runtimeTarget": { "name": ".NETCoreApp,Version=v10.0/linux-x64" },
              "targets": {
                ".NETCoreApp,Version=v10.0/linux-x64": {
                  "App/1.0.0": { "dependencies": { "runtimepack.Microsoft.NETCore.App.Runtime.linux-x64": "10.0.7" }, "runtime": { "App.dll": {} } },
                  "runtimepack.Microsoft.NETCore.App.Runtime.linux-x64/10.0.7": { "runtime": { "System.Runtime.dll": { "assemblyVersion": "10.0.0.0" } } }
                }
              },
              "libraries": {
                "App/1.0.0": { "type": "project" },
                "runtimepack.Microsoft.NETCore.App.Runtime.linux-x64/10.0.7": { "type": "runtimepack" }
              }
            }
            """);

        var inventory = HostInventory.Create(new HostEnvironment(appDeps, null, "linux-x64"), []);

        var framework = Assert.Single(inventory.SharedFrameworks);
        Assert.Equal(("Microsoft.NETCore.App", "10.0.7"), (framework.Name, framework.Version.ToNormalizedString()));
        Assert.True(inventory.IsHostOwnedAssembly("System.Runtime"));
        Assert.Null(inventory.FindPackage("runtimepack.Microsoft.NETCore.App.Runtime.linux-x64"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void SingleFileHost_WithoutDepsFiles_IsUnsupported(string? depsFiles)
    {
        var ex = Assert.Throws<UnsupportedHostException>(() => HostInventory.Create(new HostEnvironment(depsFiles, null, "linux-x64"), []));

        Assert.Contains("single-file", ex.Message);
    }

    [Fact]
    public void SingleFileHost_EntryAssemblyWithoutALocation_IsUnsupported()
    {
        // A single-file app's assemblies have no location; so does an assembly loaded from a stream.
        var context = new AssemblyLoadContext("single-file", isCollectible: true);
        try
        {
            using var bytes = File.OpenRead(typeof(PluginManifest).Assembly.Location);
            var entry = context.LoadFromStream(bytes);

            var ex = Assert.Throws<UnsupportedHostException>(() => HostInventory.Create(new HostEnvironment(DepsFiles(), entry, "linux-x64"), []));

            Assert.Contains("single-file", ex.Message);
            Assert.IsAssignableFrom<NotSupportedException>(ex);
        }
        finally
        {
            context.Unload();
        }
    }

    [Fact]
    public void MissingAppDepsFile_IsUnsupported()
    {
        var ex = Assert.Throws<UnsupportedHostException>(() =>
            HostInventory.Create(new HostEnvironment(_temp.Combine("gone.deps.json"), null, "linux-x64"), []));

        Assert.Contains("gone.deps.json", ex.Message);
    }

    [Fact]
    public void MalformedDepsFile_Throws()
    {
        var path = _temp.WriteFile("bad/app.deps.json", "{ \"targets\": {} }");

        Assert.Throws<InvalidDataException>(() => HostInventory.Create(new HostEnvironment(path, null, "linux-x64"), []));
    }

    [Fact]
    public void FromCurrentProcess_ReadsThisTestProcess()
    {
        var inventory = HostInventory.FromCurrentProcess([]);

        Assert.Contains(inventory.SharedFrameworks, f => f.Name == "Microsoft.NETCore.App" && f.Version.Major == Environment.Version.Major);
        Assert.True(inventory.IsHostOwnedAssembly("System.Private.CoreLib") || inventory.IsHostOwnedAssembly("System.Runtime"));
        Assert.True(inventory.IsHostOwnedAssembly("Alcove.Abstractions"));
        Assert.True(inventory.IsHostOwnedAssembly("Microsoft.Extensions.DependencyInjection.Abstractions"));
        Assert.True(inventory.IsHostSharedAssembly("xunit.v3.core"));
        Assert.Equal(System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier, inventory.RuntimeIdentifier);
        Assert.Equal(FrameworkConstants.CommonFrameworks.Net10_0, inventory.Framework);
    }

    [Fact]
    public void DepsFileDetection_RecognisesFrameworkFiles()
    {
        var sep = Path.DirectorySeparatorChar;
        Assert.True(DepsJsonReader.IsFrameworkDepsFile($"{sep}dotnet{sep}shared{sep}Microsoft.NETCore.App{sep}10.0.7{sep}Microsoft.NETCore.App.deps.json"));
        Assert.False(DepsJsonReader.IsFrameworkDepsFile($"{sep}app{sep}MyApp.deps.json"));
        Assert.False(DepsJsonReader.IsFrameworkDepsFile($"{sep}dotnet{sep}shared{sep}Microsoft.NETCore.App{sep}10.0.7{sep}Other.deps.json"));
    }

    private string DepsFiles()
    {
        var app = _temp.Combine("app", "MyApp.deps.json");
        if (!File.Exists(app))
        {
            _temp.WriteFile("app/MyApp.deps.json", AppDepsJson);
            _temp.WriteFile("dotnet/shared/Microsoft.NETCore.App/10.0.7/Microsoft.NETCore.App.deps.json", FrameworkDepsJson);
        }

        // ';'-separated on every OS, the app's file first.
        return string.Join(';', app, _temp.Combine("dotnet", "shared", "Microsoft.NETCore.App", "10.0.7", "Microsoft.NETCore.App.deps.json"));
    }

    private HostInventory Create(IEnumerable<string>? hostOwned = null, Assembly? entry = null) =>
        HostInventory.Create(new HostEnvironment(DepsFiles(), entry, "linux-x64"), hostOwned ?? []);
}

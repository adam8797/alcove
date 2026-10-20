using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json.Nodes;
using Alcove.Hosting;
using Alcove.IntegrationTests.Infrastructure;
using Alcove.Storage;
using Alcove.Validation;

namespace Alcove.IntegrationTests.Hosting;

/// <summary>
/// The compatibility gate (ADR 0002): Fixture.AttributePlugin is built against <c>Fixture.Contracts [1.0, 2.0)</c>.
/// The host is this test process, with a fabricated deps.json that adds Fixture.Contracts at a chosen version
/// (the contracts assembly itself is loaded into the default context, as a host would ship it).
/// </summary>
public sealed class ContractRangeTests(FixturePackages packages) : IDisposable
{
    private const string Contracts = "Fixture.Contracts";
    private const string Plugin = "Fixture.AttributePlugin";

    private static readonly Lock s_loadGate = new();
    private static bool s_contractsLoaded;

    private readonly PluginRootBuilder _root = new();
    private readonly TempDirectory _host = new("alcove-host");

    public void Dispose()
    {
        _root.Dispose();
        _host.Dispose();
    }

    [Fact]
    public async Task APluginBuiltForContracts1x_IsIncompatibleWithAHostShipping2x()
    {
        var environment = HostWithContracts("2.0.0");
        var state = await InstallAsync(environment);
        Assert.Equal("[1.0.0, 2.0.0)", Assert.Single(state.Dependencies.Single(d => d.Id == Contracts).RequestedBy).Range.ToNormalizedString());
        await using var host = PluginHost.Create(Options(), environment);

        var report = await host.ValidateAsync(TestContext.Current.CancellationToken);

        var error = Assert.Single(report.Errors);
        Assert.Equal((ValidationCodes.HostOwnedConflict, Plugin), (error.Code, error.PluginId));
        Assert.Contains("Fixture.Contracts [1.0.0, 2.0.0)", error.Message);
        Assert.Contains("the host has 2.0.0", error.Message);

        await host.LoadAsync(TestContext.Current.CancellationToken);
        var plugin = Assert.Single(host.Plugins);
        Assert.Equal(PluginStatus.NotLoaded, plugin.Status);
        Assert.Contains("incompatible", plugin.Reason);
    }

    [Fact]
    public async Task APluginBuiltForContracts1x_LoadsInAHostShipping1_5()
    {
        LoadContractsIntoTheDefaultContext();
        var environment = HostWithContracts("1.5.0");
        await InstallAsync(environment);
        await using var host = PluginHost.Create(Options(), environment);
        var added = 0;
        host.Catalog.ExtensionsAdded += (_, e) => added += e.Extensions.Count;

        Assert.Empty((await host.ValidateAsync(TestContext.Current.CancellationToken)).Issues);
        await host.LoadAsync(TestContext.Current.CancellationToken);

        var plugin = Assert.Single(host.Plugins);
        Assert.True(plugin.Status == PluginStatus.Loaded, plugin.ToString());
        Assert.Equal(2, added);   // GermanGreeter and PlainGreeter, typed by the host's Fixture.Contracts
    }

    private async Task<PluginState> InstallAsync(HostEnvironment environment)
    {
        var inventory = HostInventory.Create(environment, [Contracts]);
        var states = await _root.InstallAsync(inventory, [packages[Plugin].PackagePath!], Plugin);
        return Assert.Single(states);
    }

    private PluginHostOptions Options() => new() { Root = _root.Root, HostOwnedPackages = { Contracts } };

    /// <summary>This process's deps files, with the app's deps.json listing Fixture.Contracts at <paramref name="version"/>.</summary>
    private HostEnvironment HostWithContracts(string version)
    {
        var files = ((string)AppContext.GetData("APP_CONTEXT_DEPS_FILES")!).Split(';', StringSplitOptions.RemoveEmptyEntries);
        var appDeps = files.First(f => !DepsJsonReader.IsFrameworkDepsFile(f));
        var json = JsonNode.Parse(File.ReadAllText(appDeps))!;
        var targetName = (string)json["runtimeTarget"]!["name"]!;
        json["targets"]![targetName]![$"{Contracts}/{version}"] = new JsonObject
        {
            ["runtime"] = new JsonObject { [$"lib/net10.0/{Contracts}.dll"] = new JsonObject { ["assemblyVersion"] = "1.0.0.0" } },
        };
        json["libraries"]![$"{Contracts}/{version}"] = new JsonObject { ["type"] = "package", ["serviceable"] = true, ["sha512"] = "" };

        var fabricated = _host.Combine(Path.GetFileName(appDeps));
        File.WriteAllText(fabricated, json.ToJsonString());
        var depsFiles = string.Join(';', files.Select(f => f == appDeps ? fabricated : f));
        return new HostEnvironment(depsFiles, typeof(ContractRangeTests).Assembly, RuntimeInformation.RuntimeIdentifier);
    }

    /// <summary>Ships the contracts in the host: their assembly in the default context (once per test process).</summary>
    private void LoadContractsIntoTheDefaultContext()
    {
        lock (s_loadGate)
        {
            if (s_contractsLoaded)
            {
                return;
            }

            var target = Path.Combine(Path.GetTempPath(), $"alcove-contracts-{Guid.NewGuid():N}", $"{Contracts}.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using (var archive = ZipFile.OpenRead(packages[Contracts].PackagePath!))
            {
                archive.GetEntry($"lib/net10.0/{Contracts}.dll")!.ExtractToFile(target);
            }

            AssemblyLoadContext.Default.LoadFromAssemblyPath(target);
            s_contractsLoaded = true;
            try
            {
                Directory.Delete(Path.GetDirectoryName(target)!, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Windows keeps loaded assemblies locked; the temp folder is left behind there.
            }
        }
    }
}

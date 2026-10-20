using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Alcove.Hosting;

namespace Alcove.IntegrationTests.Infrastructure;

/// <summary>
/// A host environment like this test process's, with extra packages in the app's <c>deps.json</c> (e.g. a host that
/// ships <c>Fixture.Dep 1.0.0</c>). Their assemblies aren't loaded; only the inventory changes.
/// </summary>
internal static class FabricatedHost
{
    public static HostEnvironment With(TempDirectory directory, params (string Id, string Version, string AssemblyVersion)[] packages)
    {
        var files = ((string)AppContext.GetData("APP_CONTEXT_DEPS_FILES")!).Split(';', StringSplitOptions.RemoveEmptyEntries);
        var appDeps = files.First(f => !DepsJsonReader.IsFrameworkDepsFile(f));
        var json = JsonNode.Parse(File.ReadAllText(appDeps))!;
        var targetName = (string)json["runtimeTarget"]!["name"]!;
        foreach (var (id, version, assemblyVersion) in packages)
        {
            json["targets"]![targetName]![$"{id}/{version}"] = new JsonObject
            {
                ["runtime"] = new JsonObject { [$"lib/net10.0/{id}.dll"] = new JsonObject { ["assemblyVersion"] = assemblyVersion } },
            };
            json["libraries"]![$"{id}/{version}"] = new JsonObject { ["type"] = "package", ["serviceable"] = true, ["sha512"] = "" };
        }

        var fabricated = directory.Combine(Path.GetFileName(appDeps));
        File.WriteAllText(fabricated, json.ToJsonString());
        var depsFiles = string.Join(';', files.Select(f => f == appDeps ? fabricated : f));
        return new HostEnvironment(depsFiles, typeof(FabricatedHost).Assembly, RuntimeInformation.RuntimeIdentifier);
    }
}

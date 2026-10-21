using System.Text.Json;
using Alcove.IntegrationTests.Infrastructure;

namespace Alcove.IntegrationTests.Development;

/// <summary>
/// Alcove.Sdk's harness support with the real CLI (ADR 0017): a harness project with a <c>PluginUnderTest</c>
/// (style 1), and <c>dotnet run</c> on a plugin project that sets <c>AlcoveHarnessExecutable</c> (style 2). The harness
/// is <c>tests/fixtures/Development/Fixture.Development.Harness</c>; its plugin under test is the Hosting fixture UserB.
/// One class, so its builds of the shared Hosting workspace run one at a time.
/// </summary>
public sealed class HarnessTests(HostingFixtures fixtures)
{
    private const string Harness = "Fixture.Development.Harness";
    private const string UserA = "Fixture.Hosting.UserA";
    private const string UserB = "Fixture.Hosting.UserB";

    private static readonly SemaphoreSlim s_harnessBuilt = new(1, 1);
    private static string? s_harnessPath;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private FixtureWorkspace Workspace => fixtures.Workspace;

    [Fact]
    public async Task PluginUnderTest_BuildsThePluginFirst_ButNeverCopiesIt_AndTheHarnessLoadsItFromTheAttribute()
    {
        var harness = await BuildHarnessAsync();
        var harnessOutput = Path.GetDirectoryName(harness)!;
        var pluginOutput = Path.Combine(Workspace.ProjectDirectory(Path.Combine("Hosting", UserB)), "bin", "Debug", "net10.0");

        // Built first, with its manifest; nothing of it (or its dependencies) in the harness output.
        Assert.True(File.Exists(Path.Combine(pluginOutput, $"{UserB}.plugin.json")));
        Assert.DoesNotContain(Directory.GetFiles(harnessOutput), f => Path.GetFileName(f).StartsWith(UserB, StringComparison.OrdinalIgnoreCase));
        Assert.False(File.Exists(Path.Combine(harnessOutput, "Fixture.Dep.dll")));
        Assert.DoesNotContain(UserB, File.ReadAllText(Path.Combine(harnessOutput, $"{Harness}.deps.json")), StringComparison.OrdinalIgnoreCase);

        // The generated attribute names the build output and the project; the harness loads the plugin from it.
        var result = (await Workspace.RunAsync([harness], Ct)).EnsureSuccess();
        var dev = Assert.Single(Lines(result.Output, "DEV "));
        Assert.Equal($"{pluginOutput}{Path.DirectorySeparatorChar} project={Path.Combine(Workspace.ProjectDirectory(Path.Combine("Hosting", UserB)), $"{UserB}.csproj")}", dev);
        Assert.Equal([$"{UserB} 1.0.0 Loaded development=True"], Lines(result.Output, "PLUGIN "));
        Assert.Equal([$"{UserB}: UserB uses Dep 2.0 in '{UserB}' and NewIn2 present; services: False"], Lines(result.Output, "DESCRIBE "));
        Assert.Empty(Lines(result.Output, "ISSUE "));
    }

    [Fact]
    public async Task DotNetRun_OnAPluginProject_StartsTheHarnessWithIt_AndGeneratesALaunchProfile()
    {
        var harness = await BuildHarnessAsync();
        var plugin = Workspace.CloneFixture(Path.Combine("Hosting", UserA));
        var pluginDirectory = Workspace.ProjectDirectory(plugin);
        var pluginOutput = Path.Combine(pluginDirectory, "bin", "Debug", "net10.0");

        var result = (await Workspace.RunAsync(
            ["run", "--project", pluginDirectory, "--disable-build-servers", .. Properties($"AlcoveHarnessExecutable={harness}")], Ct)).EnsureSuccess();

        // The harness ran with this plugin's build output (and its own plugin under test).
        Assert.Contains($"DEV {pluginOutput} project=", result.Output, StringComparison.Ordinal);
        Assert.Equal([$"{UserA} 1.0.0 Loaded development=True", $"{UserB} 1.0.0 Loaded development=True"], Lines(result.Output, "PLUGIN "));
        Assert.Contains($"DESCRIBE {UserA}: UserA uses Dep 1.0 in '{UserA}'", result.Output, StringComparison.Ordinal);

        // For IDEs, the same arguments: a "Project" profile for Visual Studio (the program is RunCommand), an
        // "Executable" one for Rider.
        using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(pluginDirectory, "Properties", "launchSettings.json")));
        var profiles = settings.RootElement.GetProperty("profiles").EnumerateObject().ToList();
        Assert.Equal([$"{UserA} in harness", $"{UserA} in harness (Rider)"], profiles.Select(p => p.Name));
        Assert.Equal("Project", profiles[0].Value.GetProperty("commandName").GetString());
        Assert.False(profiles[0].Value.TryGetProperty("executablePath", out _));
        Assert.Equal("Executable", profiles[1].Value.GetProperty("commandName").GetString());
        Assert.Equal("dotnet", profiles[1].Value.GetProperty("executablePath").GetString());
        foreach (var profile in profiles)
        {
            Assert.Equal($"\"{harness}\" --alcove-dev-plugin \"{pluginOutput}\"", profile.Value.GetProperty("commandLineArgs").GetString());
            Assert.Equal(Path.GetDirectoryName(harness), profile.Value.GetProperty("workingDirectory").GetString());
        }

        // dotnet run still works with that profile in place (it takes the profile's arguments, the same ones).
        var again = (await Workspace.RunAsync(
            ["run", "--project", pluginDirectory, "--disable-build-servers", .. Properties($"AlcoveHarnessExecutable={harness}")], Ct)).EnsureSuccess();
        Assert.Contains($"{UserA} 1.0.0 Loaded development=True", again.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnExistingLaunchSettingsFile_IsKept_AndNoneIsGeneratedWithoutAHarness()
    {
        var harness = await BuildHarnessAsync();
        var plugin = Workspace.CloneFixture(Path.Combine("Hosting", UserA));
        var pluginDirectory = Workspace.ProjectDirectory(plugin);

        (await Workspace.RunAsync(["build", pluginDirectory, "--disable-build-servers", .. Properties()], Ct)).EnsureSuccess();
        Assert.False(Directory.Exists(Path.Combine(pluginDirectory, "Properties")));

        var path = Path.Combine(pluginDirectory, "Properties", "launchSettings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        const string Mine = """{ "profiles": { "mine": { "commandName": "Executable", "executablePath": "my-app" } } }""";
        File.WriteAllText(path, Mine);
        (await Workspace.RunAsync(["build", pluginDirectory, "--disable-build-servers", .. Properties($"AlcoveHarnessExecutable={harness}")], Ct)).EnsureSuccess();

        Assert.Equal(Mine, File.ReadAllText(path));
    }

    /// <summary>Builds the harness once per run (the Alcove package joins the workspace feed first).</summary>
    private async Task<string> BuildHarnessAsync()
    {
        await s_harnessBuilt.WaitAsync(Ct);
        try
        {
            if (s_harnessPath is null)
            {
                Workspace.AddToFeed(TestEnvironment.AlcovePackage("Alcove"));
                var project = Workspace.ProjectDirectory(Path.Combine("Development", Harness));
                var result = (await Workspace.RunAsync(["build", project, "--disable-build-servers", .. Properties()], Ct)).EnsureSuccess();
                Assert.DoesNotContain(": warning ", result.Output, StringComparison.OrdinalIgnoreCase);
                s_harnessPath = Path.Combine(project, "bin", "Debug", "net10.0", $"{Harness}.dll");
            }

            return s_harnessPath;
        }
        finally
        {
            s_harnessBuilt.Release();
        }
    }

    private IEnumerable<string> Properties(params string[] extra) =>
    [
        $"-p:AlcoveVersion={TestEnvironment.AlcoveVersion}",
        $"-p:RestoreConfigFile={Workspace.NuGetConfigPath}",
        .. extra.Select(p => $"-p:{p}"),
    ];

    private static List<string> Lines(string output, string prefix) =>
        output.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.StartsWith(prefix, StringComparison.Ordinal)).Select(l => l[prefix.Length..]).ToList();
}

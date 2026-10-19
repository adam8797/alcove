using System.Security;
using System.Text.Json;
using NuGet.Configuration;

namespace Alcove.IntegrationTests.Infrastructure;

/// <summary>
/// A temporary, isolated workspace for building fixture projects as a plugin developer would:
/// <list type="bullet">
///   <item>a copy of <c>tests/fixtures</c> (so builds never write into the repository);</item>
///   <item>a local folder feed holding this build's Alcove packages, plus whatever fixtures are packed into it;</item>
///   <item>its own <c>NUGET_PACKAGES</c> folder and a <c>nuget.config</c> that ignores the user's settings.</item>
/// </list>
/// Alcove.* and Fixture.* packages come only from the local feed. Third-party packages come read-only from the
/// test run's own global packages folder (restored by the solution build), so no network is needed.
/// </summary>
internal sealed class FixtureWorkspace : IDisposable
{
    private readonly TempDirectory _root;
    private readonly Dictionary<string, string> _environment;

    private FixtureWorkspace()
    {
        _root = new TempDirectory("alcove-fixtures");
        FeedDirectory = _root.Combine("feed");
        FixturesDirectory = _root.Combine("fixtures");
        NuGetConfigPath = _root.Combine("nuget.config");
        GlobalPackagesDirectory = _root.Combine(".nuget", "packages");

        Directory.CreateDirectory(FeedDirectory);
        Directory.CreateDirectory(GlobalPackagesDirectory);
        TempDirectory.CopyTree(TestEnvironment.FixturesDirectory, FixturesDirectory);
        File.Copy(Path.Combine(TestEnvironment.RepoRoot, "global.json"), _root.Combine("global.json"));
        foreach (var id in new[] { "Alcove.Abstractions", "Alcove.Sdk" })
        {
            var package = TestEnvironment.AlcovePackage(id);
            File.Copy(package, Path.Combine(FeedDirectory, Path.GetFileName(package)));
        }

        File.WriteAllText(NuGetConfigPath, CreateNuGetConfig(FeedDirectory, HostGlobalPackagesFolder()));

        _environment = new Dictionary<string, string>
        {
            ["NUGET_PACKAGES"] = GlobalPackagesDirectory,
            ["NUGET_HTTP_CACHE_PATH"] = _root.Combine(".nuget", "http-cache"),
            ["NUGET_PLUGINS_CACHE_PATH"] = _root.Combine(".nuget", "plugins-cache"),
        };
    }

    /// <summary>The workspace root.</summary>
    public string RootDirectory => _root.Path;

    /// <summary>The local folder feed.</summary>
    public string FeedDirectory { get; }

    /// <summary>The copy of <c>tests/fixtures</c>.</summary>
    public string FixturesDirectory { get; }

    /// <summary>The workspace's <c>nuget.config</c>.</summary>
    public string NuGetConfigPath { get; }

    /// <summary>The workspace's own global packages folder (<c>NUGET_PACKAGES</c>).</summary>
    public string GlobalPackagesDirectory { get; }

    /// <summary>Where third-party packages come from: the test run's global packages folder, read-only.</summary>
    public static string ThirdPartySource => HostGlobalPackagesFolder();

    /// <summary>Creates a workspace with this build's Alcove packages in its feed.</summary>
    public static FixtureWorkspace Create() => new();

    /// <summary>Copies a package into the local feed (e.g. a host contracts package built by the solution).</summary>
    public void AddToFeed(string packagePath) =>
        File.Copy(packagePath, Path.Combine(FeedDirectory, Path.GetFileName(packagePath)), overwrite: true);

    /// <summary>The directory of a fixture project.</summary>
    public string ProjectDirectory(string fixture) => Path.Combine(FixturesDirectory, fixture);

    /// <summary>
    /// Copies a fixture project to a new folder next to the others (same Directory.Build.* files), so a test
    /// can build a variant without touching the shared copy. Returns the new folder name.
    /// </summary>
    public string CloneFixture(string fixture)
    {
        var clone = $"{fixture}-{Guid.NewGuid():N}";
        TempDirectory.CopyTree(ProjectDirectory(fixture), ProjectDirectory(clone));
        return clone;
    }

    /// <summary>Packs a fixture into the feed. The result's package path is set when packing succeeded.</summary>
    public async Task<PackResult> PackAsync(string fixture, IEnumerable<string>? properties = null, CancellationToken cancellationToken = default)
    {
        var output = _root.Combine("pack-output", Guid.NewGuid().ToString("N"));
        var result = await RunAsync(
            ["pack", ProjectDirectory(fixture), "--output", output, "--disable-build-servers", .. CommonProperties(properties)],
            cancellationToken).ConfigureAwait(false);

        string? package = null;
        if (result.Succeeded)
        {
            var produced = Directory.GetFiles(output, "*.nupkg");
            Assert.True(produced.Length == 1, $"Expected one package from {fixture}, got {produced.Length}.{Environment.NewLine}{result.Output}");
            package = Path.Combine(FeedDirectory, Path.GetFileName(produced[0]));
            File.Move(produced[0], package, overwrite: true);
        }

        return new PackResult(fixture, result, package);
    }

    /// <summary>Builds a fixture (restoring first).</summary>
    public Task<ProcessResult> BuildAsync(string fixture, IEnumerable<string>? properties = null, CancellationToken cancellationToken = default) =>
        RunAsync(["build", ProjectDirectory(fixture), "--disable-build-servers", .. CommonProperties(properties)], cancellationToken);

    /// <summary>
    /// Evaluates a restored fixture project and returns MSBuild property values as evaluation sees them
    /// (before any target runs).
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> GetEvaluatedPropertiesAsync(
        string fixture, IEnumerable<string> names, CancellationToken cancellationToken = default)
    {
        var nameList = names.ToList();
        var result = (await RunAsync(
            ["msbuild", ProjectDirectory(fixture), "-nologo", "-nodeReuse:false", .. nameList.Select(n => $"-getProperty:{n}"), .. CommonProperties(null)],
            cancellationToken).ConfigureAwait(false)).EnsureSuccess();

        if (nameList.Count == 1)
        {
            return new Dictionary<string, string> { [nameList[0]] = result.Output.Trim() };
        }

        using var json = JsonDocument.Parse(result.Output);
        return json.RootElement.GetProperty("Properties").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetString() ?? "");
    }

    /// <summary>Runs <c>dotnet</c> in the workspace with its isolated NuGet environment.</summary>
    public Task<ProcessResult> RunAsync(IEnumerable<string> arguments, CancellationToken cancellationToken = default) =>
        DotNetCli.RunAsync(arguments, RootDirectory, _environment, cancellationToken);

    public void Dispose() => _root.Dispose();

    /// <summary>Deletes the workspace, leaving files that are still loaded (see <see cref="TempDirectory.DisposeBestEffort"/>).</summary>
    public void DisposeBestEffort() => _root.DisposeBestEffort();

    private IEnumerable<string> CommonProperties(IEnumerable<string>? extra) =>
    [
        $"-p:AlcoveVersion={TestEnvironment.AlcoveVersion}",
        $"-p:RestoreConfigFile={NuGetConfigPath}",
        .. (extra ?? []).Select(p => $"-p:{p}"),
    ];

    private static string HostGlobalPackagesFolder() =>
        SettingsUtility.GetGlobalPackagesFolder(Settings.LoadDefaultSettings(root: null));

    private static string CreateNuGetConfig(string feed, string thirdPartySource) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <configuration>
          <packageSources>
            <clear />
            <add key="alcove-test-feed" value="{SecurityElement.Escape(feed)}" />
            <add key="third-party" value="{SecurityElement.Escape(thirdPartySource)}" />
          </packageSources>
          <packageSourceMapping>
            <clear />
            <packageSource key="alcove-test-feed">
              <package pattern="Alcove" />
              <package pattern="Alcove.*" />
              <package pattern="Fixture.*" />
            </packageSource>
            <packageSource key="third-party">
              <package pattern="*" />
            </packageSource>
          </packageSourceMapping>
          <disabledPackageSources>
            <clear />
          </disabledPackageSources>
          <fallbackPackageFolders>
            <clear />
          </fallbackPackageFolders>
        </configuration>
        """;
}

/// <summary>The result of packing a fixture.</summary>
internal sealed record PackResult(string Fixture, ProcessResult Process, string? PackagePath)
{
    /// <summary>The packed package; fails the test if packing failed.</summary>
    public PluginPackageReader OpenPackage()
    {
        Process.EnsureSuccess();
        return new PluginPackageReader(PackagePath!);
    }
}

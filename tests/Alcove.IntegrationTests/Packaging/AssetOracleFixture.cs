using System.Text.Json;
using Alcove.IntegrationTests.Infrastructure;
using NuGet.Configuration;

namespace Alcove.IntegrationTests.Packaging;

/// <summary>
/// Builds a small app referencing packages with interesting assets, once per runtime identifier, so the
/// <c>deps.json</c> written by <c>dotnet build</c> can serve as the oracle for asset selection.
/// </summary>
/// <remarks>
/// Fixture packages are packed into the workspace's feed. Third-party packages come read-only from the test
/// run's global packages folder; one that isn't there is left out (and its test cases are skipped), so no
/// network is needed.
/// </remarks>
public sealed class AssetOracleFixture : IAsyncLifetime
{
    /// <summary>Runtime identifiers to compare; <see cref="Portable"/> means a build without <c>-r</c>.</summary>
    public static readonly string[] RuntimeIdentifiers = [Portable, "linux-x64", "linux-musl-x64", "win-x64", "osx-arm64"];

    /// <summary>The packages compared: fixtures (always available) and third-party packages (when available offline).</summary>
    public static readonly (string Id, string Version)[] Packages =
    [
        ("Fixture.Assets", "1.0.0"),
        ("Fixture.Library", "1.0.0"),
        ("Microsoft.Extensions.DependencyInjection.Abstractions", "8.0.0"),
        ("Humanizer.Core", "2.14.1"),
    ];

    public const string Portable = "portable";

    private readonly Dictionary<string, string> _depsFiles = new(StringComparer.Ordinal);
    private FixtureWorkspace? _workspace;

    internal FixtureWorkspace Workspace => _workspace ?? throw new InvalidOperationException("Not initialized.");

    /// <summary>Packages left out because they aren't available offline, with the reason.</summary>
    public Dictionary<string, string> Unavailable { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The packed Fixture.Assets package.</summary>
    public string AssetsPackage { get; private set; } = "";

    /// <summary>The oracle's <c>deps.json</c> for a runtime identifier.</summary>
    public JsonDocument ReadDepsJson(string runtimeIdentifier) => JsonDocument.Parse(File.ReadAllBytes(_depsFiles[runtimeIdentifier]));

    /// <summary>Where restore extracted a package.</summary>
    public string PackageDirectory(string id, string version) =>
        Path.Combine(Workspace.GlobalPackagesDirectory, id.ToLowerInvariant(), version.ToLowerInvariant());

    public async ValueTask InitializeAsync()
    {
        _workspace = FixtureWorkspace.Create();
        var ct = TestContext.Current.CancellationToken;
        foreach (var fixture in new[] { "Fixture.Assets", "Fixture.Library" })
        {
            var packed = await _workspace.PackAsync(fixture, cancellationToken: ct);
            packed.Process.EnsureSuccess();
            if (fixture == "Fixture.Assets")
            {
                AssetsPackage = packed.PackagePath!;
            }
        }

        var hostPackages = SettingsUtility.GetGlobalPackagesFolder(Settings.LoadDefaultSettings(root: null));
        var references = new List<(string Id, string Version)>();
        foreach (var (id, version) in Packages)
        {
            if (!id.StartsWith("Fixture.", StringComparison.Ordinal) && !Directory.Exists(Path.Combine(hostPackages, id.ToLowerInvariant(), version)))
            {
                Unavailable[id] = $"{id} {version} isn't in the global packages folder '{hostPackages}' and the tests don't use the network";
                continue;
            }

            references.Add((id, version));
        }

        var builds = RuntimeIdentifiers.Select(rid => BuildOracleAsync(rid, references, ct));
        foreach (var (rid, depsFile) in await Task.WhenAll(builds))
        {
            _depsFiles[rid] = depsFile;
        }
    }

    public ValueTask DisposeAsync()
    {
        _workspace?.DisposeBestEffort();
        return ValueTask.CompletedTask;
    }

    private async Task<(string Rid, string DepsFile)> BuildOracleAsync(string rid, IEnumerable<(string Id, string Version)> references, CancellationToken cancellationToken)
    {
        // Under the fixtures folder, so it gets the fixtures' Directory.Build.* (warnings as errors, no CPM).
        var name = $"Oracle-{rid}";
        var directory = Workspace.ProjectDirectory(name);
        Directory.CreateDirectory(directory);
        var packageReferences = string.Join(Environment.NewLine, references.Select(r => $"""    <PackageReference Include="{r.Id}" Version="{r.Version}" />"""));
        await File.WriteAllTextAsync(Path.Combine(directory, name + ".csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <UseAppHost>false</UseAppHost>
                <SatelliteResourceLanguages>de;fr</SatelliteResourceLanguages>
              </PropertyGroup>
              <ItemGroup>
            {packageReferences}
              </ItemGroup>
            </Project>
            """, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(directory, "Program.cs"), "System.Console.WriteLine(\"oracle\");", cancellationToken);

        var output = Path.Combine(Workspace.RootDirectory, "oracle-out", rid);
        var properties = rid == Portable ? [] : new[] { $"RuntimeIdentifier={rid}", "SelfContained=false" };
        var result = await Workspace.RunAsync(
            ["build", directory, "--output", output, "--disable-build-servers", $"-p:AlcoveVersion={TestEnvironment.AlcoveVersion}", $"-p:RestoreConfigFile={Workspace.NuGetConfigPath}", .. properties.Select(p => $"-p:{p}")],
            cancellationToken);
        result.EnsureSuccess();
        return (rid, Path.Combine(output, name + ".deps.json"));
    }
}

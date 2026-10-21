namespace Alcove.IntegrationTests.Infrastructure;

/// <summary>
/// Packs the plugin fixtures under <c>tests/fixtures/Hosting</c> once per test run, against
/// <c>Fixture.HostContracts</c>, which this test process references (so it's in the host's deps.json and is
/// host-owned). Tests install the packages into their own plugin roots without a feed.
/// </summary>
public sealed class HostingFixtures : IAsyncLifetime
{
    /// <summary>The host-owned contracts package the fixture plugins build against.</summary>
    public const string ContractsPackage = "Fixture.HostContracts";

    private FixtureWorkspace? _workspace;

    /// <summary>The folder holding every packed fixture (plugins and the Fixture.Dep library versions).</summary>
    public string PackagesDirectory { get; private set; } = "";

    /// <summary>The workspace the fixtures were packed in (its feed holds the Alcove packages and the contracts).</summary>
    internal FixtureWorkspace Workspace => _workspace ?? throw new InvalidOperationException("Not initialized.");

    /// <summary>Every packed fixture.</summary>
    public IReadOnlyList<string> Packages => Directory.GetFiles(PackagesDirectory, "*.nupkg");

    public async ValueTask InitializeAsync()
    {
        _workspace = FixtureWorkspace.Create();
        _workspace.AddToFeed(TestEnvironment.AlcovePackage(ContractsPackage));
        PackagesDirectory = Path.Combine(_workspace.RootDirectory, "hosting-packages");
        var result = await _workspace.RunAsync(
            [
                "pack", Path.Combine(_workspace.FixturesDirectory, "Hosting", "Hosting.slnx"),
                "--output", PackagesDirectory, "--disable-build-servers",
                $"-p:AlcoveVersion={TestEnvironment.AlcoveVersion}", $"-p:RestoreConfigFile={_workspace.NuGetConfigPath}",
            ],
            TestContext.Current.CancellationToken);
        result.EnsureSuccess();
        Assert.DoesNotContain(": warning ", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    public ValueTask DisposeAsync()
    {
        _workspace?.DisposeBestEffort();   // development plugin tests load fixture assemblies from the workspace in place
        return ValueTask.CompletedTask;
    }
}

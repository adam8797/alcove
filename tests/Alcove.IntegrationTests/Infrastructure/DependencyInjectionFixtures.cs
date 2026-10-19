namespace Alcove.IntegrationTests.Infrastructure;

/// <summary>
/// Packs the plugin fixtures under <c>tests/fixtures/DependencyInjection</c> once per test run, against
/// <c>Fixture.HostContracts</c> and this build's <c>Alcove.DependencyInjection</c> and <c>Alcove.Autofac</c> packages
/// (which this test process references, so they're in the host's deps.json). Tests install the packages into their own
/// plugin roots without a feed, or hot-install them from the folder as a feed.
/// </summary>
public sealed class DependencyInjectionFixtures : IAsyncLifetime
{
    private FixtureWorkspace? _workspace;

    /// <summary>The folder holding every packed fixture.</summary>
    public string PackagesDirectory { get; private set; } = "";

    /// <summary>The workspace the fixtures were packed in.</summary>
    internal FixtureWorkspace Workspace => _workspace ?? throw new InvalidOperationException("Not initialized.");

    /// <summary>Every packed fixture.</summary>
    public IReadOnlyList<string> Packages => Directory.GetFiles(PackagesDirectory, "*.nupkg");

    public async ValueTask InitializeAsync()
    {
        _workspace = FixtureWorkspace.Create();
        foreach (var id in new[] { HostingFixtures.ContractsPackage, "Alcove", "Alcove.DependencyInjection", "Alcove.Autofac" })
        {
            _workspace.AddToFeed(TestEnvironment.AlcovePackage(id));
        }

        PackagesDirectory = Path.Combine(_workspace.RootDirectory, "di-packages");
        var result = await _workspace.RunAsync(
            [
                "pack", Path.Combine(_workspace.FixturesDirectory, "DependencyInjection", "DependencyInjection.slnx"),
                "--output", PackagesDirectory, "--disable-build-servers",
                $"-p:AlcoveVersion={TestEnvironment.AlcoveVersion}", $"-p:RestoreConfigFile={_workspace.NuGetConfigPath}",
            ],
            TestContext.Current.CancellationToken);
        result.EnsureSuccess();
        Assert.DoesNotContain(": warning ", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    public ValueTask DisposeAsync()
    {
        _workspace?.Dispose();
        return ValueTask.CompletedTask;
    }
}

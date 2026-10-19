namespace Alcove.IntegrationTests.Infrastructure;

/// <summary>
/// Packs the lifecycle fixtures under <c>tests/fixtures/Lifecycle</c> once per test run, against
/// <c>Fixture.HostContracts</c> (host-owned, as in <see cref="HostingFixtures"/>): Fixture.Lifecycle.Versioned 1.0.0,
/// 2.0.0 and 3.0.0 (built against Fixture.Dep 1.0, 2.0 and 3.0), the three Fixture.Dep versions, and
/// Fixture.Lifecycle.User, which depends on Fixture.Lifecycle.Versioned [1.0.0, 3.0.0).
/// </summary>
public sealed class LifecycleFixtures : IAsyncLifetime
{
    /// <summary>The plugin packed in three versions.</summary>
    public const string Versioned = "Fixture.Lifecycle.Versioned";

    /// <summary>The plugin depending on <see cref="Versioned"/> [1.0.0, 3.0.0).</summary>
    public const string User = "Fixture.Lifecycle.User";

    private FixtureWorkspace? _workspace;

    /// <summary>The folder holding every packed fixture: use it as a folder feed.</summary>
    public string PackagesDirectory { get; private set; } = "";

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        _workspace = FixtureWorkspace.Create();
        _workspace.AddToFeed(TestEnvironment.AlcovePackage(HostingFixtures.ContractsPackage));
        PackagesDirectory = Path.Combine(_workspace.RootDirectory, "lifecycle-packages");
        string[] common = [$"-p:AlcoveVersion={TestEnvironment.AlcoveVersion}", $"-p:RestoreConfigFile={_workspace.NuGetConfigPath}", "--disable-build-servers"];
        var lifecycle = Path.Combine(_workspace.FixturesDirectory, "Lifecycle");

        var versions = await _workspace.RunAsync(["pack", Path.Combine(lifecycle, "Lifecycle.slnx"), "--output", PackagesDirectory, .. common], ct);
        versions.EnsureSuccess();
        Assert.DoesNotContain(": warning ", versions.Output, StringComparison.OrdinalIgnoreCase);

        // The dependent restores the versions it depends on from the workspace feed.
        foreach (var package in Directory.GetFiles(PackagesDirectory, "*.nupkg"))
        {
            _workspace.AddToFeed(package);
        }

        var user = await _workspace.RunAsync(["pack", Path.Combine(lifecycle, User), "--output", PackagesDirectory, .. common], ct);
        user.EnsureSuccess();
        Assert.DoesNotContain(": warning ", user.Output, StringComparison.OrdinalIgnoreCase);
    }

    public ValueTask DisposeAsync()
    {
        _workspace?.Dispose();
        return ValueTask.CompletedTask;
    }
}

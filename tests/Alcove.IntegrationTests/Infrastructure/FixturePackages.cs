namespace Alcove.IntegrationTests.Infrastructure;

/// <summary>
/// Packs the fixture contracts, library and plugins once per test run into one <see cref="FixtureWorkspace"/>.
/// Tests read the results; tests that need a variant build use <see cref="FixtureWorkspace.CloneFixture"/>.
/// </summary>
public sealed class FixturePackages : IAsyncLifetime
{
    /// <summary>Packed first: the plugins depend on them.</summary>
    public static readonly string[] Libraries = ["Fixture.Contracts", "Fixture.Library"];

    /// <summary>Plugin fixtures packed with Alcove.Sdk.</summary>
    public static readonly string[] Plugins =
        ["Fixture.EntryPointPlugin", "Fixture.AttributePlugin", "Fixture.CellPlugin", "Fixture.IsolatedPlugin"];

    private readonly Dictionary<string, PackResult> _results = new(StringComparer.Ordinal);
    private FixtureWorkspace? _workspace;

    internal FixtureWorkspace Workspace => _workspace ?? throw new InvalidOperationException("Not initialized.");

    internal PackResult this[string fixture] => _results[fixture];

    public async ValueTask InitializeAsync()
    {
        _workspace = FixtureWorkspace.Create();
        var ct = TestContext.Current.CancellationToken;

        foreach (var library in Libraries)
        {
            var result = await _workspace.PackAsync(library, cancellationToken: ct);
            result.Process.EnsureSuccess();
            _results[library] = result;
        }

        var plugins = await Task.WhenAll(Plugins.Select(p => _workspace.PackAsync(p, cancellationToken: ct)));
        foreach (var result in plugins)
        {
            _results[result.Fixture] = result;
        }
    }

    public ValueTask DisposeAsync()
    {
        _workspace?.Dispose();
        return ValueTask.CompletedTask;
    }
}

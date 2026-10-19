using Alcove.IntegrationTests.Infrastructure;

namespace Alcove.Wpf.Tests.Windows;

/// <summary>
/// Packs the WPF plugin fixtures (tests/fixtures/Wpf) once per test run, on Windows only, and runs the WPF test host
/// (tests/Alcove.Wpf.TestHost) for the scenarios.
/// </summary>
public sealed class WpfFixtures : IAsyncLifetime
{
    /// <summary>Why the WPF scenarios can't run here, or <see langword="null"/> on Windows.</summary>
    public static string? SkipReason { get; } = OperatingSystem.IsWindows()
        ? null
        : "WPF only runs on Windows: these scenarios run on the Windows CI job.";

    private FixtureWorkspace? _workspace;

    /// <summary>The folder feed holding the packed fixtures.</summary>
    public string PackagesDirectory { get; private set; } = "";

    public async ValueTask InitializeAsync()
    {
        if (SkipReason is not null)
        {
            return;
        }

        _workspace = FixtureWorkspace.Create();
        foreach (var id in new[] { "Alcove", "Alcove.Wpf", "Fixture.Wpf.HostSdk" })
        {
            _workspace.AddToFeed(TestEnvironment.AlcovePackage(id));
        }

        PackagesDirectory = Path.Combine(_workspace.RootDirectory, "wpf-packages");
        var result = await _workspace.RunAsync(
            [
                "pack", Path.Combine(_workspace.FixturesDirectory, "Wpf", "Wpf.slnx"),
                "--output", PackagesDirectory, "--disable-build-servers",
                $"-p:AlcoveVersion={TestEnvironment.AlcoveVersion}", $"-p:RestoreConfigFile={_workspace.NuGetConfigPath}",
            ],
            TestContext.Current.CancellationToken);
        result.EnsureSuccess();
        Assert.DoesNotContain(": warning ", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    public ValueTask DisposeAsync()
    {
        _workspace?.DisposeBestEffort();
        return ValueTask.CompletedTask;
    }

    /// <summary>Installs plugins into a new plugin root (in a test host process of its own) and returns the root.</summary>
    internal async Task<string> PrepareRootAsync(TempDirectory directory, params string[] plugins)
    {
        var root = directory.Combine("root");
        var result = await RunHostAsync(["prepare", "--root", root, "--feed", PackagesDirectory, "--install", string.Join(',', plugins)]);
        Assert.True(result.ExitCode == 0, $"Preparing the plugin root failed:{Environment.NewLine}{result.Output}");
        return root;
    }

    /// <summary>Runs a scenario in its own test host process and returns its <c>CHECK</c> lines.</summary>
    public async Task<ScenarioResult> RunAsync(string scenario, string root, params string[] options)
    {
        var result = await RunHostAsync(["run", scenario, "--root", root, "--feed", PackagesDirectory, .. options]);
        Assert.True(result.ExitCode == 0, $"Scenario '{scenario}' failed (exit code {result.ExitCode}):{Environment.NewLine}{result.Output}");
        return ScenarioResult.Parse(result.Output);
    }

    private static Task<ProcessResult> RunHostAsync(IEnumerable<string> arguments) =>
        DotNetCli.RunAsync([TestHostPath, .. arguments], Path.GetDirectoryName(TestHostPath)!, new Dictionary<string, string>(), TestContext.Current.CancellationToken);

    private static string TestHostPath { get; } =
        typeof(WpfFixtures).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>().Single(a => a.Key == "AlcoveWpfTestHost").Value!;
}

/// <summary>The <c>CHECK name=value</c> lines of a scenario, with the full output for failure messages.</summary>
public sealed class ScenarioResult
{
    private readonly Dictionary<string, string> _checks;

    private ScenarioResult(Dictionary<string, string> checks, string output)
    {
        _checks = checks;
        Output = output;
    }

    public string Output { get; }

    public string? this[string name] => _checks.GetValueOrDefault(name);

    public static ScenarioResult Parse(string output)
    {
        var checks = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.StartsWith("CHECK ", StringComparison.Ordinal)))
        {
            var separator = line.IndexOf('=', StringComparison.Ordinal);
            checks[line[6..separator]] = line[(separator + 1)..];
        }

        return new ScenarioResult(checks, output);
    }

    /// <summary>Asserts a check's value, showing the whole output when it differs.</summary>
    public void Expect(string name, string expected) =>
        Assert.True(this[name] == expected, $"CHECK {name}: expected '{expected}', got '{this[name] ?? "(missing)"}'.{Environment.NewLine}{Output}");

    /// <summary>Asserts a check's value contains a text.</summary>
    public void ExpectContains(string name, string expected) =>
        Assert.True(this[name]?.Contains(expected, StringComparison.Ordinal) == true, $"CHECK {name}: expected it to contain '{expected}', got '{this[name] ?? "(missing)"}'.{Environment.NewLine}{Output}");
}

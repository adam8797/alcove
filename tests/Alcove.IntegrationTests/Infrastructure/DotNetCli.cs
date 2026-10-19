using System.Diagnostics;
using System.Text;

namespace Alcove.IntegrationTests.Infrastructure;

/// <summary>The result of running the <c>dotnet</c> CLI.</summary>
internal sealed record ProcessResult(int ExitCode, string Output, string CommandLine)
{
    public bool Succeeded => ExitCode == 0;

    /// <summary>Fails the test with the full output unless the command succeeded.</summary>
    public ProcessResult EnsureSuccess()
    {
        Assert.True(Succeeded, $"'{CommandLine}' failed with exit code {ExitCode}:{Environment.NewLine}{Output}");
        return this;
    }
}

/// <summary>
/// Runs the <c>dotnet</c> CLI in a clean environment: MSBuild and test-host variables inherited from the
/// test run are removed, so a nested build behaves like one started from a fresh shell.
/// </summary>
internal static class DotNetCli
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    /// <summary>The <c>dotnet</c> executable running this test, or the one on PATH.</summary>
    public static string Executable { get; } =
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host) ? host : "dotnet";

    public static async Task<ProcessResult> RunAsync(
        IEnumerable<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(Executable)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var name in startInfo.Environment.Keys.ToList())
        {
            if (name.StartsWith("MSBUILD", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("VSTEST", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("NUGET", StringComparison.OrdinalIgnoreCase)
                || name.Equals("DOTNET_CLI_UI_LANGUAGE", StringComparison.OrdinalIgnoreCase))
            {
                startInfo.Environment.Remove(name);
            }
        }

        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        startInfo.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        startInfo.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        foreach (var (name, value) in environment)
        {
            startInfo.Environment[name] = value;
        }

        var commandLine = $"dotnet {string.Join(' ', startInfo.ArgumentList)}";
        var output = new StringBuilder();
        using var process = new Process { StartInfo = startInfo };
        process.OutputDataReceived += (_, e) => Append(output, e.Data);
        process.ErrorDataReceived += (_, e) => Append(output, e.Data);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"'{commandLine}' didn't finish within {Timeout} or was cancelled.{Environment.NewLine}{output}");
        }

        process.WaitForExit();   // flush redirected output
        return new ProcessResult(process.ExitCode, output.ToString(), commandLine);
    }

    private static void Append(StringBuilder output, string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (output)
        {
            output.AppendLine(line);
        }
    }
}

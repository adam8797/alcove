using System.Diagnostics;

namespace Alcove.IntegrationTests.Infrastructure;

/// <summary>The cell scenario fixtures, built once for every test class that uses them.</summary>
[CollectionDefinition(Name)]
public sealed class CellFixturesCollection : ICollectionFixture<CellFixtures>
{
    public const string Name = "Cell scenarios";
}

/// <summary>
/// Builds the cell scenario fixtures (<c>tests/fixtures/Cells</c>) once per test run, and
/// the native test library with gcc when it's available. Each project's output is in its own folder.
/// </summary>
public sealed class CellFixtures : IAsyncLifetime
{
    private FixtureWorkspace? _workspace;

    /// <summary>The build output root: one folder per fixture project.</summary>
    public string OutputDirectory { get; private set; } = "";

    /// <summary>Why the native library couldn't be built, or <see langword="null"/> if it was.</summary>
    public string? NativeUnavailableReason { get; private set; }

    /// <summary>The path of a fixture assembly, e.g. <c>Assembly("Fixture.UserA")</c> or <c>Assembly("Fixture.Dep.V2", "Fixture.Dep")</c>.</summary>
    public string Assembly(string project, string? assemblyName = null) =>
        Path.Combine(OutputDirectory, project, (assemblyName ?? project) + ".dll");

    /// <summary>The native test library <c>alcovenat</c> built with <c>ALCOVENAT_VERSION=<paramref name="version"/></c>.</summary>
    public string NativeLibrary(int version) =>
        Path.Combine(OutputDirectory, "native", $"v{version}", OperatingSystem.IsMacOS() ? "libalcovenat.dylib" : "libalcovenat.so");

    public async ValueTask InitializeAsync()
    {
        _workspace = FixtureWorkspace.Create();
        OutputDirectory = Path.Combine(_workspace.RootDirectory, "cells-out") + Path.DirectorySeparatorChar;
        var ct = TestContext.Current.CancellationToken;

        var build = await _workspace.BuildAsync(Path.Combine("Cells", "Cells.slnx"), [$"CellFixturesOutput={OutputDirectory}"], ct);
        build.EnsureSuccess();
        Assert.DoesNotContain(": warning ", build.Output, StringComparison.OrdinalIgnoreCase);

        NativeUnavailableReason = await BuildNativeAsync(ct);
    }

    public ValueTask DisposeAsync()
    {
        _workspace?.DisposeBestEffort();   // the cells keep the fixture assemblies loaded
        return ValueTask.CompletedTask;
    }

    private async Task<string?> BuildNativeAsync(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            return "the native scenarios build a shared library with gcc, which these tests only do on Linux and macOS";
        }

        var source = Path.Combine(_workspace!.FixturesDirectory, "Cells", "native", "alcovenat.c");
        foreach (var version in new[] { 1, 2 })
        {
            var output = NativeLibrary(version);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            var startInfo = new ProcessStartInfo("gcc") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var argument in new[] { "-shared", "-fPIC", $"-DALCOVENAT_VERSION={version}", "-o", output, source })
            {
                startInfo.ArgumentList.Add(argument);
            }

            try
            {
                using var gcc = Process.Start(startInfo)!;
                var errors = await gcc.StandardError.ReadToEndAsync(cancellationToken);
                await gcc.WaitForExitAsync(cancellationToken);
                if (gcc.ExitCode != 0)
                {
                    return $"gcc failed: {errors}";
                }
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                return $"gcc isn't available ({ex.Message})";
            }
        }

        return null;
    }
}

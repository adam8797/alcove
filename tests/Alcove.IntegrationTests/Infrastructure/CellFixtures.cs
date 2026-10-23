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
/// the native test library when a C compiler is available: gcc, or MSVC on Windows (found with vswhere). Each project's
/// output is in its own folder.
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
        Path.Combine(OutputDirectory, "native", $"v{version}",
            OperatingSystem.IsWindows() ? "alcovenat.dll" : OperatingSystem.IsMacOS() ? "libalcovenat.dylib" : "libalcovenat.so");

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
        var source = Path.Combine(_workspace!.FixturesDirectory, "Cells", "native", "alcovenat.c");
        var vcvars = OperatingSystem.IsWindows() ? FindVcVars() : null;
        if (OperatingSystem.IsWindows() && vcvars is null)
        {
            return "the native scenarios need MSVC (the C++ build tools) on Windows, and vswhere found none";
        }

        foreach (var version in new[] { 1, 2 })
        {
            var output = NativeLibrary(version);
            var directory = Path.GetDirectoryName(output)!;
            Directory.CreateDirectory(directory);
            ProcessStartInfo startInfo;
            if (vcvars is not null)
            {
                // cl needs vcvars64's environment, so both run in one cmd script. "\\" before a closing quote is one
                // backslash to cl.
                var script = Path.Combine(directory, "build.cmd");
                File.WriteAllText(script, $"""
                    @call "{vcvars}" >nul || exit /b 1
                    cl /nologo /LD /DALCOVENAT_VERSION={version} "{source}" /Fo"{directory}\\" /Fe"{output}" /link /EXPORT:alcovenat_version
                    """);
                startInfo = new ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", script } };
            }
            else
            {
                startInfo = new ProcessStartInfo("gcc") { ArgumentList = { "-shared", "-fPIC", $"-DALCOVENAT_VERSION={version}", "-o", output, source } };
            }

            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.UseShellExecute = false;
            var compiler = vcvars is null ? "gcc" : "cl";
            try
            {
                using var process = Process.Start(startInfo)!;
                var outputText = process.StandardOutput.ReadToEndAsync(cancellationToken);
                var errors = await process.StandardError.ReadToEndAsync(cancellationToken);
                await process.WaitForExitAsync(cancellationToken);
                if (process.ExitCode != 0)
                {
                    return $"{compiler} failed: {errors}{await outputText}";
                }
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                return $"{compiler} isn't available ({ex.Message})";
            }
        }

        return null;
    }

    /// <summary>The newest Visual Studio's <c>vcvars64.bat</c> with the C++ x64 tools, or <see langword="null"/>.</summary>
    private static string? FindVcVars()
    {
        var vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (!File.Exists(vswhere))
        {
            return null;
        }

        var startInfo = new ProcessStartInfo(vswhere)
        {
            ArgumentList = { "-latest", "-products", "*", "-requires", "Microsoft.VisualStudio.Component.VC.Tools.x86.x64", "-find", @"VC\Auxiliary\Build\vcvars64.bat" },
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(startInfo)!;
        var path = process.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        process.WaitForExit();
        return path is not null && File.Exists(path) ? path : null;
    }
}

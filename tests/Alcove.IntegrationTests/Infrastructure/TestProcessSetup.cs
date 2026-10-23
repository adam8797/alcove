using System.Runtime.CompilerServices;

namespace Alcove.IntegrationTests.Infrastructure;

/// <summary>Process-wide settings for the test run, applied before any test.</summary>
internal static class TestProcessSetup
{
    /// <summary>
    /// On Windows, NuGet retries a certificate chain that ends in an untrusted root 3 times, 1 s apart (its
    /// <c>ChainBuildPolicy</c>), in case Windows is still updating its roots. The trust tests sign with self-signed
    /// certificates, so every such verification took 3 s for the same verdict. Nested <c>dotnet</c> runs don't see
    /// this (<see cref="DotNetCli"/> removes <c>NUGET*</c> variables).
    /// </summary>
    [ModuleInitializer]
    internal static void DisableNuGetChainBuildRetries() =>
        Environment.SetEnvironmentVariable("NUGET_EXPERIMENTAL_CHAIN_BUILD_RETRY_POLICY", "0,0");
}

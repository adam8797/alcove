using System.Reflection;
using System.Runtime.InteropServices;

namespace Alcove.Hosting;

/// <summary>
/// What the host inventory is read from. <see cref="Current"/> describes the running process; tests pass
/// their own deps files and entry assembly.
/// </summary>
/// <param name="DepsFiles">The value of <c>APP_CONTEXT_DEPS_FILES</c>: <c>;</c>-separated on every OS, the app's <c>deps.json</c> first, then one per shared framework.</param>
/// <param name="EntryAssembly">The host's entry assembly, whose framework attributes give the host framework.</param>
/// <param name="RuntimeIdentifier">The runtime identifier used to select RID-specific assets.</param>
internal sealed record HostEnvironment(string? DepsFiles, Assembly? EntryAssembly, string RuntimeIdentifier)
{
    /// <summary>The running process.</summary>
    public static HostEnvironment Current =>
        new(AppContext.GetData("APP_CONTEXT_DEPS_FILES") as string, Assembly.GetEntryAssembly(), RuntimeInformation.RuntimeIdentifier);
}

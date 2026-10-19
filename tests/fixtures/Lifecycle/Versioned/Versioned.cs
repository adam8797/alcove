using Alcove;
using Fixture.HostContracts;

namespace Fixture.Lifecycle.Versioned;

/// <summary>Which version of the plugin is running (read at run time, not compiled into callers).</summary>
public static class VersionInfo
{
#if V3
    private const string Value = "3.0";
#elif V2
    private const string Value = "2.0";
#else
    private const string Value = "1.0";
#endif

    public static string Version() => Value;

    public static string Describe() => "Versioned " + Version();
}

[Extension(typeof(IDescriber))]
public sealed class VersionedDescriber : IDescriber
{
    public string Describe() => $"{VersionInfo.Describe()} uses {FixtureDep.Info.Describe()}";
}

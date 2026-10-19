using System.Runtime.CompilerServices;
using FixtureMiddle;
using FixtureProvider;

namespace FixtureTop;

/// <summary>Depends (as a plugin) only on Middle, but references the provider's types through Middle's API.</summary>
public static class Top
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string ServiceName() => Middle.GetService().Name;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool SameServiceType() => typeof(ProviderService) == Middle.ServiceType;
}

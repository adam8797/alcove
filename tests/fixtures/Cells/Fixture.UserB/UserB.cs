using System.Runtime.CompilerServices;
using FixtureDep;

namespace FixtureUserB;

/// <summary>Built against Dep 2.0.</summary>
public static class UserB
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string UseD() => Info.Describe();

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string UseNewIn2() => Info.NewIn2();

    public static Type BaseThingType => typeof(BaseThing);
}

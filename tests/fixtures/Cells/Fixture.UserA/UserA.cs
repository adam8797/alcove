using System.Runtime.CompilerServices;
using FixtureDep;

namespace FixtureUserA;

/// <summary>Built against Dep 1.0.</summary>
public static class UserA
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string UseD() => Info.Describe();

    public static Type BaseThingType => typeof(BaseThing);
}

public class ADerived : BaseThing
{
    public override string Who() => "ADerived";
}

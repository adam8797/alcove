using System.Runtime.CompilerServices;
using FixtureDep;
using FixtureProvider;

namespace FixtureConsumer3;

/// <summary>Uses the provider's leaky API but is built against Dep 3.0.</summary>
public static class Consumer3
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string UseV3() => Widgets.Make().V3Api();
}

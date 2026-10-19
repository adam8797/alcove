using System.Runtime.CompilerServices;
using FixtureDep;
using FixtureProvider;

namespace FixtureConsumer2;

/// <summary>Uses the provider's leaky API; built against Dep 2.0.</summary>
public static class Consumer2
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string ServiceName() => ProviderService.Create().Name;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string InspectOwnWidget() => Widgets.InspectObject(new Widget());

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string CastProviderWidget() => "cast ok: " + ((Widget)Widgets.MakeAsObject()).Describe();

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string PassWidget() => Widgets.Inspect(new Widget());
}

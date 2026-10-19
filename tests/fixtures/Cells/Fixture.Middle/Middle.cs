using FixtureProvider;

namespace FixtureMiddle;

/// <summary>Exposes the provider's types in its own API (a plugin depending on a plugin).</summary>
public static class Middle
{
    public static ProviderService GetService() => ProviderService.Create();

    public static Type ServiceType => typeof(ProviderService);
}

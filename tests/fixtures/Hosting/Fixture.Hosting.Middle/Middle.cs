using Alcove;
using Fixture.HostContracts;
using Fixture.Hosting.Provider;

namespace Fixture.Hosting.Middle;

public static class Middle
{
    public static ProviderService GetService() => ProviderService.Create();

    public static Type ServiceType => typeof(ProviderService);
}

[Extension(typeof(IDescriber))]
public sealed class MiddleDescriber : IDescriber
{
    public string Describe() => "Middle: " + Middle.GetService().Name;
}

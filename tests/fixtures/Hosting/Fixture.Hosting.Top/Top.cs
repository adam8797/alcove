using Alcove;
using Fixture.HostContracts;
using Fixture.Hosting.Provider;

namespace Fixture.Hosting.Top;

[Extension(typeof(IDescriber))]
public sealed class TopDescriber : IDescriber
{
    public string Describe() =>
        $"Top: {Middle.Middle.GetService().Name}; same ProviderService type: {typeof(ProviderService) == Middle.Middle.ServiceType}";
}

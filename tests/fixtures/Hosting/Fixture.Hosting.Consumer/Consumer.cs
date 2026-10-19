using Alcove;
using FixtureDep;
using Fixture.HostContracts;
using Fixture.Hosting.Provider;

namespace Fixture.Hosting.Consumer;

[Extension(typeof(IDescriber))]
public sealed class ConsumerDescriber : IDescriber
{
    public string Describe() => "Consumer: " + Widgets.InspectObject(new Widget()) + "; " + ProviderService.Create().Name;
}

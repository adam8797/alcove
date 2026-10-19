using Alcove;
using Fixture.HostContracts;
using Fixture.Hosting.Provider;

namespace Fixture.Hosting.Consumer3;

[Extension(typeof(IDescriber))]
public sealed class Consumer3Describer : IDescriber
{
    public string Describe() => "Consumer3: " + Widgets.Make().V3Api();
}

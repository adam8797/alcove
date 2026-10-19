using Alcove;
using Fixture.HostContracts;

namespace Fixture.Hosting.Isolated;

[Extension(typeof(IDescriber))]
public sealed class IsolatedDescriber : IDescriber
{
    public string Describe() => "Isolated";
}

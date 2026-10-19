using Alcove;
using Fixture.HostContracts;

namespace Fixture.Hosting.AfterFaulty;

[Extension(typeof(IDescriber))]
public sealed class AfterFaultyDescriber : IDescriber
{
    public string Describe() => "AfterFaulty: " + Faulty.FaultyApi.Hello();
}

using Alcove;
using Fixture.HostContracts;

namespace Fixture.Hosting.HookUser;

[Extension(typeof(IDescriber))]
public sealed class HookUserDescriber : IDescriber
{
    public string Describe() => "HookUser after " + new Fixture.Hosting.Hook.HookDescriber().Describe();
}

using Alcove;
using Fixture.HostContracts;
using Fixture.Lifecycle.Versioned;

namespace Fixture.Lifecycle.User;

[Extension(typeof(IDescriber))]
public sealed class UserDescriber : IDescriber
{
    public string Describe() => "User sees " + VersionInfo.Describe();
}

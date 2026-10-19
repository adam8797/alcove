using Alcove;
using Fixture.HostContracts;

namespace Fixture.Hosting.UserA;

[Extension(typeof(IDescriber))]
public sealed class UserADescriber : IDescriber
{
    public string Describe() => "UserA uses " + FixtureDep.Info.Describe();
}

[Greeter(Language = "de", Lifetime = ExtensionLifetime.Transient)]
public sealed class GermanGreeter : IGreeter
{
    public string Greet(string name) => $"Hallo, {name}";
}

// Not an extension: implementing the interface isn't enough.
public sealed class UnmarkedDescriber : IDescriber
{
    public string Describe() => "never discovered";
}

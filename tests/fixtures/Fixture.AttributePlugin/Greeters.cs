using Alcove;
using Fixture.Contracts;

namespace Fixture.AttributePlugin;

[Greeter(Language = "de", Lifetime = ExtensionLifetime.Transient)]
public sealed class GermanGreeter : IGreeter
{
    public string Greet(string name) => $"Hallo, {name}";
}

[Extension(typeof(IGreeter))]
public sealed class PlainGreeter : IGreeter
{
    public string Greet(string name) => $"Hi, {name}";
}

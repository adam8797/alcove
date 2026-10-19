using Fixture.Contracts;
using Fixture.Library;

namespace Fixture.CellPlugin;

[Greeter(Language = "en-loud")]
public sealed class LoudGreeter : IGreeter
{
    public string Greet(string name) => Punctuation.Exclaim($"HELLO, {name.ToUpperInvariant()}");
}

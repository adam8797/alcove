using ConsoleHost.Contracts;
using Sample.Text;

namespace Sample.Greetings;

[Command(Name = "greet", Description = "Says hello (uses Sample.Text 1.0)")]
public sealed class GreetCommand : ICommand
{
    public string Run() => $"Hello from {TextInfo.Describe()}";
}

using ConsoleHost.Contracts;

namespace Sample.Time;

[Command(Name = "time", Description = "Tells the time (run me with dotnet run: I start the harness)")]
public sealed class TimeCommand : ICommand
{
    public string Run() => $"It's {DateTime.Now:HH:mm}, from {typeof(TimeCommand).Assembly.Location}";
}

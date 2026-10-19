using Alcove;

namespace ConsoleHost.Contracts;

/// <summary>The console host's extension point: a command it lists and runs.</summary>
public interface ICommand
{
    /// <summary>Runs the command and returns its output.</summary>
    string Run();
}

/// <summary>Metadata of an <see cref="ICommand"/>, readable without creating it.</summary>
public sealed class CommandAttribute : ExtensionAttribute
{
    public CommandAttribute() : base(typeof(ICommand)) { }

    /// <summary>The command's name.</summary>
    public required string Name { get; init; }

    /// <summary>What it does.</summary>
    public string? Description { get; init; }
}

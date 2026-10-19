using Alcove;

namespace Fixture.Contracts;

/// <summary>Metadata for <see cref="IGreeter"/> extensions (the app developer guide's ToolWindowAttribute shape).</summary>
public sealed class GreeterAttribute : ExtensionAttribute
{
    public GreeterAttribute() : base(typeof(IGreeter)) { }

    public required string Language { get; init; }

    public string? Description { get; init; }
}

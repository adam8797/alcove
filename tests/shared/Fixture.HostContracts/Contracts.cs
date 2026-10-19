using Alcove;

namespace Fixture.HostContracts;

/// <summary>An extension point: says who it is and where its code runs (versions, cells).</summary>
public interface IDescriber
{
    string Describe();
}

/// <summary>An extension point with typed metadata.</summary>
public interface IGreeter
{
    string Greet(string name);
}

/// <summary>Metadata for <see cref="IGreeter"/> extensions (the guides' ToolWindowAttribute shape).</summary>
public sealed class GreeterAttribute : ExtensionAttribute
{
    public GreeterAttribute() : base(typeof(IGreeter)) { }

    public required string Language { get; init; }
}

/// <summary>A host service plugins get from IPluginContext.Services: they record what happens to them.</summary>
public interface IEventLog
{
    void Write(string entry);
}

/// <summary>A service plugins register in their cell (dependency injection add-ons); the host registers one too.</summary>
public interface ICellService
{
    string Name { get; }
}

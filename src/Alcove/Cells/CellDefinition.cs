namespace Alcove.Cells;

/// <summary>
/// A named cell defined by the app: the listed plugins share one load context. The app's options win over a
/// plugin's own cell request, but an isolated plugin can't be put in a cell with others.
/// </summary>
public sealed class CellDefinition
{
    /// <summary>Creates a cell definition.</summary>
    /// <param name="name">The cell name.</param>
    public CellDefinition(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    /// <summary>The cell name.</summary>
    public string Name { get; }

    /// <summary>IDs of the plugins that go into this cell.</summary>
    public IList<string> Plugins { get; init => field = value ?? new List<string>(); } = new List<string>();
}

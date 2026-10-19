namespace Alcove.Internal.Cells;

/// <summary>A native file name that two cells load from different paths.</summary>
internal sealed record NativeLibraryCollision(string FileName, string Path, string Cell, string OtherPath, string OtherCell);

/// <summary>
/// Remembers which native library files the cells have loaded, by file name. A native library's own
/// dependencies are matched by file name (soname on Linux) against everything already loaded in the
/// process, so two cells loading different files with the same name may end up sharing one.
/// </summary>
internal sealed class NativeLibraryRegistry
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, (string Path, string Cell)> _loaded = new(CellPlan.NativeFileNameComparer);
    private readonly List<NativeLibraryCollision> _collisions = [];

    /// <summary>Collisions found so far.</summary>
    public IReadOnlyList<NativeLibraryCollision> Collisions
    {
        get
        {
            lock (_gate)
            {
                return _collisions.ToList();
            }
        }
    }

    /// <summary>Records that <paramref name="cell"/> loaded <paramref name="path"/>; returns a collision if another cell loaded a different file with the same name.</summary>
    public NativeLibraryCollision? Register(string path, string cell)
    {
        var fileName = Path.GetFileName(path);
        lock (_gate)
        {
            if (!_loaded.TryGetValue(fileName, out var existing))
            {
                _loaded[fileName] = (path, cell);
                return null;
            }

            if (string.Equals(existing.Path, path, StringComparison.Ordinal) || string.Equals(existing.Cell, cell, StringComparison.Ordinal))
            {
                return null;
            }

            var collision = new NativeLibraryCollision(fileName, path, cell, existing.Path, existing.Cell);
            _collisions.Add(collision);
            return collision;
        }
    }
}

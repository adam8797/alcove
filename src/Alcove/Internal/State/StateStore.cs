using System.Text.Json;
using Alcove.Internal.IO;
using Alcove.Internal.Json;

namespace Alcove.Internal.State;

/// <summary>
/// Reads and writes <c>&lt;root&gt;/state.json</c>. Reading needs no lock; writing needs the root lock and is
/// atomic (temporary file, then rename).
/// </summary>
internal sealed class StateStore
{
    /// <summary>The state file name in the plugin root.</summary>
    public const string FileName = "state.json";

    /// <summary>Creates a store for a plugin root.</summary>
    public StateStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
        FilePath = Path.Combine(Root, FileName);
    }

    /// <summary>The plugin root.</summary>
    public string Root { get; }

    /// <summary>The state file's path.</summary>
    public string FilePath { get; }

    /// <summary>Reads the state file, or returns <see cref="StateDocument.Empty"/> if there is none yet.</summary>
    /// <exception cref="InvalidDataException">The file is malformed or written by a newer, incompatible Alcove.</exception>
    public StateDocument Read()
    {
        byte[] content;
        try
        {
            content = AtomicFile.ReadAllBytes(FilePath);
        }
        catch (FileNotFoundException)
        {
            return StateDocument.Empty;
        }
        catch (DirectoryNotFoundException)
        {
            return StateDocument.Empty;
        }

        return Parse(content, FilePath);
    }

    /// <summary>Writes the state file atomically. The caller must hold the root lock.</summary>
    public void Write(RootLock rootLock, StateDocument state)
    {
        ArgumentNullException.ThrowIfNull(rootLock);
        ArgumentNullException.ThrowIfNull(state);
        rootLock.EnsureHeldFor(Root);
        AtomicFile.WriteAllBytes(FilePath, Serialize(state));
    }

    /// <summary>Serialises a state document (indented UTF-8 JSON).</summary>
    public static byte[] Serialize(StateDocument state) =>
        JsonSerializer.SerializeToUtf8Bytes(state, AlcoveJsonContext.Default.StateDocument);

    /// <summary>Parses a state document.</summary>
    /// <exception cref="InvalidDataException">The content is malformed or has a newer schema version.</exception>
    public static StateDocument Parse(ReadOnlySpan<byte> content, string source = "state.json")
    {
        StateDocument? state;
        try
        {
            state = JsonSerializer.Deserialize(content, AlcoveJsonContext.Default.StateDocument);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"The state file '{source}' is malformed: {ex.Message}", ex);
        }

        if (state is null)
        {
            throw new InvalidDataException($"The state file '{source}' is empty (JSON 'null').");
        }

        if (state.SchemaVersion > StateDocument.CurrentSchemaVersion || state.SchemaVersion < 1)
        {
            throw new InvalidDataException(
                $"The state file '{source}' has schema version {state.SchemaVersion}; this version of Alcove reads version {StateDocument.CurrentSchemaVersion}.");
        }

        if (FindInvalidPackageId(state) is { } invalid)
        {
            throw new InvalidDataException($"The state file '{source}' has an invalid package ID: '{invalid}'.");
        }

        return state;
    }

    /// <summary>
    /// The first package ID that becomes part of a path (plugins, their previous and staged versions, cell-private and
    /// plugin dependencies) and that NuGet wouldn't accept, e.g. one with <c>..</c> or a path separator.
    /// </summary>
    private static string? FindInvalidPackageId(StateDocument state)
    {
        var pending = new Stack<PluginState>(state.Plugins.Where(p => p is not null));
        while (pending.TryPop(out var plugin))
        {
            if (!PackageLayout.IsValidPackageId(plugin.Id))
            {
                return plugin.Id ?? "(none)";
            }

            foreach (var dependency in plugin.Dependencies.Where(d => d.Classification is DependencyClass.CellPrivate or DependencyClass.Plugin))
            {
                if (!PackageLayout.IsValidPackageId(dependency.Id))
                {
                    return dependency.Id ?? "(none)";
                }
            }

            foreach (var previous in plugin.PreviousVersions)
            {
                pending.Push(previous);
            }

            if (plugin.StagedChange?.Target is { } target)
            {
                pending.Push(target);
            }
        }

        return null;
    }
}

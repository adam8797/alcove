namespace Alcove.Internal.Loading;

/// <summary>Where a plugin's cell assignment came from (architecture §5, rules in order).</summary>
internal enum CellSource
{
    /// <summary>1. A cell the app's options define and that lists the plugin. The app wins.</summary>
    App,

    /// <summary>2. The cell the plugin's manifest asks for.</summary>
    Manifest,

    /// <summary>3. A default cell holding only this plugin, named after it.</summary>
    Default,
}

/// <summary>A plugin to assign: its ID and what its manifest asks for.</summary>
/// <param name="Id">The plugin ID.</param>
/// <param name="RequestedCell">The manifest's <c>cell</c>, or <see langword="null"/>.</param>
/// <param name="Isolated">The manifest's <c>isolated</c> flag.</param>
internal sealed record CellCandidate(string Id, string? RequestedCell, bool Isolated);

/// <summary>A cell rule violation found while assigning.</summary>
internal sealed record CellIssue(ValidationSeverity Severity, string Code, string PluginId, string Cell, string Message);

/// <summary>The result of <see cref="CellAssigner.Assign"/>.</summary>
/// <param name="Cells">Plugin ID → cell name (case-insensitive IDs; cell names are case-sensitive).</param>
/// <param name="Sources">Plugin ID → why it got that cell.</param>
/// <param name="Issues">Rule violations. An <see cref="ValidationSeverity.Error"/> blocks the plugin.</param>
internal sealed record CellAssignment(
    IReadOnlyDictionary<string, string> Cells,
    IReadOnlyDictionary<string, CellSource> Sources,
    IReadOnlyList<CellIssue> Issues)
{
    /// <summary>The plugins of each cell.</summary>
    public ILookup<string, string> Members => Cells.ToLookup(c => c.Value, c => c.Key, StringComparer.Ordinal);
}

/// <summary>
/// Assigns plugins to cells: the app's cell definitions, then the manifest's request, then a default cell named
/// after the plugin. An isolated plugin must end up alone; if anything (even the app) puts others with it,
/// every plugin of that cell gets an error rather than a silent override.
/// </summary>
internal static class CellAssigner
{
    public static CellAssignment Assign(IEnumerable<CellCandidate> plugins, IEnumerable<CellDefinition> appCells)
    {
        ArgumentNullException.ThrowIfNull(plugins);
        ArgumentNullException.ThrowIfNull(appCells);

        var candidates = plugins.ToList();
        var issues = new List<CellIssue>();
        var fromApp = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in appCells)
        {
            foreach (var pluginId in definition.Plugins.Where(p => !string.IsNullOrWhiteSpace(p)))
            {
                if (fromApp.TryGetValue(pluginId, out var first))
                {
                    if (!string.Equals(first, definition.Name, StringComparison.Ordinal))
                    {
                        issues.Add(new CellIssue(ValidationSeverity.Warning, ValidationCodes.CellDefinitionConflict, pluginId, first,
                            $"The app's options put {pluginId} in cell '{first}' and in cell '{definition.Name}'; '{first}' is used."));
                    }

                    continue;
                }

                fromApp[pluginId] = definition.Name;
            }
        }

        var cells = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sources = new Dictionary<string, CellSource>(StringComparer.OrdinalIgnoreCase);
        foreach (var plugin in candidates)
        {
            if (fromApp.TryGetValue(plugin.Id, out var appCell))
            {
                (cells[plugin.Id], sources[plugin.Id]) = (appCell, CellSource.App);
            }
            else if (!string.IsNullOrWhiteSpace(plugin.RequestedCell))
            {
                (cells[plugin.Id], sources[plugin.Id]) = (plugin.RequestedCell, CellSource.Manifest);
            }
            else
            {
                (cells[plugin.Id], sources[plugin.Id]) = (plugin.Id, CellSource.Default);
            }
        }

        var assignment = new CellAssignment(cells, sources, issues);
        foreach (var cell in assignment.Members.Where(m => m.Skip(1).Any()))
        {
            var isolated = candidates.Where(c => c.Isolated && cell.Contains(c.Id, StringComparer.OrdinalIgnoreCase)).Select(c => c.Id).ToList();
            if (isolated.Count == 0)
            {
                continue;
            }

            var members = cell.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var member in members)
            {
                var why = string.Join(", ", isolated.Select(i => $"{i} ({Describe(sources[i])})"));
                issues.Add(new CellIssue(ValidationSeverity.Error, ValidationCodes.IsolatedPluginNotAlone, member, cell.Key,
                    $"Cell '{cell.Key}' holds {string.Join(", ", members)}, but {why} is isolated and must be alone in its cell. None of the cell's plugins load until the assignment is fixed."));
            }
        }

        return assignment;
    }

    private static string Describe(CellSource source) => source switch
    {
        CellSource.App => "put there by the app's options",
        CellSource.Manifest => "asked for it in its manifest",
        _ => "default cell",
    };
}

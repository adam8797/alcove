using Alcove.Cells;
using Alcove.Validation;

namespace Alcove.Install;

/// <summary>A name whose answer in a running cell would have to change.</summary>
/// <param name="Cell">The cell.</param>
/// <param name="Name">The assembly simple name (satellites as <c>name/culture</c>) or native library file name.</param>
/// <param name="Committed">What the cell committed to (planned or already given).</param>
/// <param name="Needed">What the new resolution needs.</param>
internal sealed record HotLoadConflict(string Cell, string Name, CellAnswer Committed, CellAnswer Needed)
{
    public override string ToString() => $"{Name} in cell '{Cell}': committed to {Committed}, the install needs {Needed}";
}

/// <summary>
/// The hot-load rule (architecture §6, ADR 0005): each name in a cell's new plan is checked against the cell's
/// committed answers. It's OK if the cell never answered it, only failed on it, or would answer identically (same
/// source and package version; for native libraries the same path). Anything else means <c>PendingRestart</c>.
/// </summary>
internal static class HotLoadRule
{
    /// <summary>The issues validation of the would-be state found that validation of the current state didn't.</summary>
    public static IReadOnlyList<ValidationIssue> Added(ValidationReport before, ValidationReport after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var known = before.Issues.ToHashSet();
        return after.Issues.Where(i => !known.Contains(i)).ToList();
    }

    /// <summary>
    /// Reasons, among the issues an install added, why it can't load into the running process: a same-identity
    /// duplicate of a WPF-relevant assembly (<see cref="ValidationCodes.WpfDuplicateWillBreak"/>) would break plugins
    /// already running, because WPF resolves by name to the copy loaded last. "May break" duplicates don't stop a hot load.
    /// </summary>
    public static IReadOnlyList<string> BreakingIssues(IEnumerable<ValidationIssue> added)
    {
        ArgumentNullException.ThrowIfNull(added);
        return added
            .Where(i => i.Code == ValidationCodes.WpfDuplicateWillBreak)
            .Select(i => i.Message)
            .Distinct(StringComparer.Ordinal)
            .Select(m => $"It would load a duplicate of a WPF-relevant assembly that breaks plugins already running: {m}")
            .ToList();
    }

    /// <summary>The names of <paramref name="plan"/> whose committed answer differs. Empty: the plan can be merged.</summary>
    public static IReadOnlyList<HotLoadConflict> Check(string cell, CommittedAnswers committed, CellPlan plan)
    {
        ArgumentNullException.ThrowIfNull(committed);
        ArgumentNullException.ThrowIfNull(plan);
        var conflicts = new List<HotLoadConflict>();
        Compare(committed.Assemblies, plan.GetPlannedAssemblyAnswers());
        Compare(committed.NativeLibraries, plan.GetPlannedNativeAnswers());
        return conflicts;

        void Compare(IReadOnlyDictionary<string, CellAnswer> current, Dictionary<string, CellAnswer> needed)
        {
            foreach (var (name, answer) in needed.OrderBy(n => n.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (current.TryGetValue(name, out var given) && given.Source != AnswerSource.Failed && !given.SameAs(answer))
                {
                    conflicts.Add(new HotLoadConflict(cell, name, given, answer));
                }
            }
        }
    }
}

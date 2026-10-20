using Alcove.Trust;

namespace Alcove.Validation;

/// <summary>How serious a validation issue is.</summary>
public enum ValidationSeverity
{
    /// <summary>Worth knowing; nothing is blocked.</summary>
    Warning,

    /// <summary>The plugin concerned (and the plugins depending on it) won't load. A global error fails <see cref="PluginHost.LoadAsync"/>.</summary>
    Error,
}

/// <summary>One finding of validation.</summary>
/// <param name="Severity">How serious it is.</param>
/// <param name="Code">A stable code from <see cref="ValidationCodes"/> (e.g. <see cref="ValidationCodes.ExposedDependencyConflict"/>) or, for a trust policy issue, from <see cref="TrustIssueCodes"/>.</param>
/// <param name="Message">A human-readable description.</param>
/// <param name="PluginId">The plugin concerned, or <see langword="null"/> for a cell-wide or global issue.</param>
/// <param name="Cell">The cell concerned, or <see langword="null"/> if none.</param>
public sealed record ValidationIssue(ValidationSeverity Severity, string Code, string Message, string? PluginId = null, string? Cell = null)
{
    /// <summary>Whether the issue concerns no plugin and no cell (e.g. an unreadable state file).</summary>
    public bool IsGlobal => PluginId is null && Cell is null;

    /// <inheritdoc />
    public override string ToString() =>
        $"{Severity} {Code}{(PluginId is null ? "" : $" [{PluginId}]")}{(Cell is null ? "" : $" (cell '{Cell}')")}: {Message}";
}

/// <summary>
/// The result of validation: local checks of the plugin root against the state file, the host and the cell
/// rules. It never uses the network. Errors stop the plugins concerned (and their dependents) from loading,
/// not the whole host; a global error makes <see cref="PluginHost.LoadAsync"/> fail.
/// </summary>
public sealed class ValidationReport
{
    internal ValidationReport(IReadOnlyList<ValidationIssue> issues)
    {
        Issues = issues;
    }

    /// <summary>Every issue, in the order found.</summary>
    public IReadOnlyList<ValidationIssue> Issues { get; }

    /// <summary>Whether there's at least one error.</summary>
    public bool HasErrors => Issues.Any(i => i.Severity == ValidationSeverity.Error);

    /// <summary>Whether there's an error that concerns no plugin or cell (loading is impossible).</summary>
    public bool HasGlobalErrors => Issues.Any(i => i.Severity == ValidationSeverity.Error && i.IsGlobal);

    /// <summary>The errors.</summary>
    public IReadOnlyList<ValidationIssue> Errors => [.. Issues.Where(i => i.Severity == ValidationSeverity.Error)];

    /// <summary>The warnings.</summary>
    public IReadOnlyList<ValidationIssue> Warnings => [.. Issues.Where(i => i.Severity == ValidationSeverity.Warning)];

    /// <summary>The issues concerning one plugin.</summary>
    /// <param name="pluginId">The plugin ID (case-insensitive).</param>
    public IReadOnlyList<ValidationIssue> ForPlugin(string pluginId) =>
        [.. Issues.Where(i => string.Equals(i.PluginId, pluginId, StringComparison.OrdinalIgnoreCase))];

    /// <inheritdoc />
    public override string ToString() =>
        Issues.Count == 0 ? "No issues." : string.Join(Environment.NewLine, Issues);
}

/// <summary>The codes of <see cref="ValidationIssue"/>s.</summary>
public static class ValidationCodes
{
    /// <summary>The state file can't be read (malformed, or written by a newer Alcove): a global error. As a warning: it lists a plugin twice.</summary>
    public const string StateFileUnreadable = nameof(StateFileUnreadable);

    /// <summary>A package the plugin needs isn't in the plugin root.</summary>
    public const string PackageMissing = nameof(PackageMissing);

    /// <summary>An extracted file recorded in the state file is gone.</summary>
    public const string FileMissing = nameof(FileMissing);

    /// <summary>An extracted file's size or timestamp differs from the state file.</summary>
    public const string FileChanged = nameof(FileChanged);

    /// <summary>The plugin package has no plugin manifest.</summary>
    public const string ManifestMissing = nameof(ManifestMissing);

    /// <summary>The plugin manifest can't be read.</summary>
    public const string ManifestInvalid = nameof(ManifestInvalid);

    /// <summary>The plugin manifest's schema version is newer than this Alcove supports.</summary>
    public const string ManifestSchemaUnsupported = nameof(ManifestSchemaUnsupported);

    /// <summary>
    /// The host's version of a host-owned package (e.g. the contracts) doesn't satisfy the version range the plugin
    /// (or one of its dependencies) declares: the plugin is incompatible with this host.
    /// </summary>
    public const string HostOwnedConflict = nameof(HostOwnedConflict);

    /// <summary>The plugin needs a host-owned package the host doesn't carry: it's incompatible.</summary>
    public const string HostOwnedMissing = nameof(HostOwnedMissing);

    /// <summary>A dependency resolved from the host now has a lower version in the host; the plugin should be resolved again.</summary>
    public const string HostSharedDowngraded = nameof(HostSharedDowngraded);

    /// <summary>
    /// A dependency resolved from the host no longer satisfies the declared version range (the host changed or
    /// dropped it), and it would still resolve to the host's copy. The plugin must be resolved again (reinstalled).
    /// </summary>
    public const string HostSharedConflict = nameof(HostSharedConflict);

    /// <summary>Plugins in one cell resolved different versions of a package; the highest is used.</summary>
    public const string CellVersionConflict = nameof(CellVersionConflict);

    /// <summary>A plugin needs a higher version of another plugin's exposed dependency than that plugin's cell has.</summary>
    public const string ExposedDependencyConflict = nameof(ExposedDependencyConflict);

    /// <summary>An isolated plugin was put in a cell with other plugins. Every plugin of that cell is blocked.</summary>
    public const string IsolatedPluginNotAlone = nameof(IsolatedPluginNotAlone);

    /// <summary>The app's options put a plugin in more than one cell; the first is used.</summary>
    public const string CellDefinitionConflict = nameof(CellDefinitionConflict);

    /// <summary>A plugin depends on a plugin that isn't installed.</summary>
    public const string PluginDependencyMissing = nameof(PluginDependencyMissing);

    /// <summary>A plugin depends on a plugin that is disabled.</summary>
    public const string PluginDependencyDisabled = nameof(PluginDependencyDisabled);

    /// <summary>A plugin declares a version range on another plugin that the installed version of that plugin doesn't satisfy.</summary>
    public const string PluginDependencyConflict = nameof(PluginDependencyConflict);

    /// <summary>Plugins depend on each other in a cycle.</summary>
    public const string PluginDependencyCycle = nameof(PluginDependencyCycle);

    /// <summary>A plugin depends (directly or not) on a plugin that validation blocked.</summary>
    public const string PluginDependencyNotLoadable = nameof(PluginDependencyNotLoadable);

    /// <summary>A WPF-relevant assembly name is loaded in several load contexts, and WPF will resolve some of its XAML to the wrong copy.</summary>
    public const string WpfDuplicateWillBreak = nameof(WpfDuplicateWillBreak);

    /// <summary>A WPF-relevant assembly name is loaded in several load contexts with different versions; name-only lookups go to the newest copy.</summary>
    public const string WpfDuplicateMayBreak = nameof(WpfDuplicateMayBreak);

    /// <summary>Cells load different native libraries with the same file name, so their own dependencies may be shared.</summary>
    public const string NativeLibraryCollision = nameof(NativeLibraryCollision);

    /// <summary><see cref="PluginHost.VerifyAsync(bool, CancellationToken)"/>: a package's <c>.nupkg</c> or <c>.nupkg.metadata</c> doesn't have the <c>contentHash</c> the state file pins.</summary>
    public const string ContentHashMismatch = nameof(ContentHashMismatch);

    /// <summary><see cref="PluginHost.VerifyAsync(bool, CancellationToken)"/>: an extracted file's content (SHA-512) differs from the package's.</summary>
    public const string FileContentChanged = nameof(FileContentChanged);

    /// <summary><see cref="PluginHost.VerifyAsync(bool, CancellationToken)"/>: a package folder holds a file that isn't in the package.</summary>
    public const string UnexpectedFile = nameof(UnexpectedFile);

    /// <summary>
    /// The plugin's manifest names no entry point, and its assemblies have more than one public, non-abstract class
    /// implementing <see cref="IPlugin"/>: set <c>PluginEntryPoint</c> in its project.
    /// </summary>
    public const string EntryPointAmbiguous = nameof(EntryPointAmbiguous);
}

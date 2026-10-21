namespace Alcove.Development;

/// <summary>
/// A <b>development plugin</b>: a plugin loaded straight from its build output folder (e.g. <c>bin/Debug/net10.0/</c>)
/// instead of the plugin root, for the plugin developer's inner loop. Add it to
/// <see cref="PluginHostOptions.DevelopmentPlugins"/>, only from a harness (see
/// <see cref="DevelopmentPluginExtensions.AddDevelopmentPluginsFrom(PluginHostOptions, IReadOnlyList{string}, System.Collections.IDictionary?)"/>).
/// </summary>
/// <remarks>
/// It's classified, validated and loaded into a cell like an installed plugin, from its <c>deps.json</c> and the manifest
/// <c>Alcove.Sdk</c> writes into the build output, in place (nothing is copied). It's never recorded in the state file,
/// trust-verified, cleaned up or staged. An installed plugin with the same ID is left on disk but not loaded in this run.
/// </remarks>
public sealed class DevelopmentPlugin
{
    /// <summary>Creates a development plugin.</summary>
    /// <param name="outputDirectory">
    /// The build output folder holding the plugin assembly, its <c>deps.json</c> and <c>&lt;PackageId&gt;.plugin.json</c>.
    /// A relative path is relative to the current directory when the host validates.
    /// </param>
    /// <param name="projectPath">The plugin's project file, if known: only used in messages.</param>
    /// <exception cref="ArgumentException"><paramref name="outputDirectory"/> is empty.</exception>
    public DevelopmentPlugin(string outputDirectory, string? projectPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        OutputDirectory = outputDirectory;
        ProjectPath = string.IsNullOrWhiteSpace(projectPath) ? null : projectPath;
    }

    /// <summary>The build output folder.</summary>
    public string OutputDirectory { get; }

    /// <summary>The plugin's project file, or <see langword="null"/>.</summary>
    public string? ProjectPath { get; }

    /// <inheritdoc />
    public override string ToString() => ProjectPath is null ? OutputDirectory : $"{OutputDirectory} ({ProjectPath})";
}

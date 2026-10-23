namespace Alcove.Development;

/// <summary>
/// Names a development plugin in a harness assembly. <c>Alcove.Sdk</c> generates one per <c>PluginUnderTest</c> item
/// of a harness project; <see cref="DevelopmentPluginExtensions.AddDevelopmentPluginsFrom(PluginHostOptions, IReadOnlyList{string}, System.Collections.IDictionary?)"/>
/// reads them from the entry assembly.
/// </summary>
/// <param name="outputDirectory">The plugin's build output folder.</param>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true, Inherited = false)]
public sealed class AlcoveDevelopmentPluginAttribute(string outputDirectory) : Attribute
{
    /// <summary>The plugin's build output folder.</summary>
    public string OutputDirectory { get; } = outputDirectory;

    /// <summary>The plugin's project file, if known.</summary>
    public string? ProjectPath { get; set; }
}

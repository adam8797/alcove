using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using Alcove.Development;

namespace Alcove.Tests.Development;

/// <summary>
/// The harness convention (ADR 0017): <c>--alcove-dev-plugin</c>, <c>ALCOVE_DEV_PLUGINS</c> and the entry assembly's
/// <see cref="AlcoveDevelopmentPluginAttribute"/>s, read only when the app asks.
/// </summary>
public sealed class DevelopmentPluginExtensionsTests
{
    private static readonly string Here = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "alcove-dev"));

    [Fact]
    public void NothingIsAdded_WithoutArgumentsEnvironmentOrAttributes()
    {
        var options = new PluginHostOptions();

        var added = options.AddDevelopmentPluginsFrom(["--other", "x"], new Hashtable(), entryAssembly: null);

        Assert.Empty(added);
        Assert.Empty(options.DevelopmentPlugins);
    }

    [Fact]
    public void TheOption_IsRepeatable_InBothForms_AndPathsBecomeAbsolute()
    {
        var options = new PluginHostOptions();

        var added = options.AddDevelopmentPluginsFrom(
            ["--alcove-dev-plugin", Path.Combine(Here, "a"), "app-arg", $"--alcove-dev-plugin={Path.Combine(Here, "b")}", "--alcove-dev-plugin", "relative/c"],
            environment: null,
            entryAssembly: null);

        Assert.Equal([Path.Combine(Here, "a"), Path.Combine(Here, "b"), Path.GetFullPath("relative/c")], added.Select(p => p.OutputDirectory));
        Assert.Equal(added, options.DevelopmentPlugins);
        Assert.All(added, p => Assert.Null(p.ProjectPath));
    }

    [Theory]
    [InlineData("--alcove-dev-plugin")]
    [InlineData("--alcove-dev-plugin=")]
    public void TheOption_WithoutAPath_Throws(string arg)
    {
        var options = new PluginHostOptions();

        var error = Assert.Throws<ArgumentException>(() => options.AddDevelopmentPluginsFrom(["x", arg], null, null));

        Assert.Contains("--alcove-dev-plugin", error.Message);
    }

    [Fact]
    public void TheEnvironmentVariable_ListsFolders_SeparatedByThePathSeparator()
    {
        var options = new PluginHostOptions();
        var environment = new Hashtable
        {
            ["ALCOVE_DEV_PLUGINS"] = string.Join(Path.PathSeparator, Path.Combine(Here, "a"), "", $" {Path.Combine(Here, "b")} "),
        };

        var added = options.AddDevelopmentPluginsFrom([], environment, null);

        Assert.Equal([Path.Combine(Here, "a"), Path.Combine(Here, "b")], added.Select(p => p.OutputDirectory));
    }

    [Fact]
    public void TheEntryAssemblysAttributes_AreRead_WithTheirProjectPaths()
    {
        var assembly = AssemblyWith(
            (Path.Combine(Here, "plugin", "bin") + Path.DirectorySeparatorChar, Path.Combine(Here, "plugin", "Plugin.csproj")),
            (Path.Combine(Here, "other"), null));
        var options = new PluginHostOptions();

        var added = options.AddDevelopmentPluginsFrom([], null, assembly);

        Assert.Equal(2, added.Count);
        Assert.Equal(Path.Combine(Here, "plugin", "bin") + Path.DirectorySeparatorChar, added[0].OutputDirectory);
        Assert.Equal(Path.Combine(Here, "plugin", "Plugin.csproj"), added[0].ProjectPath);
        Assert.Equal(Path.Combine(Here, "other"), added[1].OutputDirectory);
        Assert.Null(added[1].ProjectPath);
    }

    [Fact]
    public void AFolderNamedTwice_IsAddedOnce_AndOptionsComeFirst()
    {
        var options = new PluginHostOptions();
        options.DevelopmentPlugins.Add(new DevelopmentPlugin(Path.Combine(Here, "existing")));
        var assembly = AssemblyWith((Path.Combine(Here, "a") + Path.DirectorySeparatorChar, null), (Path.Combine(Here, "c"), null));
        var environment = new Hashtable { ["ALCOVE_DEV_PLUGINS"] = Path.Combine(Here, "a") + Path.PathSeparator + Path.Combine(Here, "existing") };

        var added = options.AddDevelopmentPluginsFrom(["--alcove-dev-plugin", Path.Combine(Here, "a"), "--alcove-dev-plugin", Path.Combine(Here, "b")], environment, assembly);

        Assert.Equal([Path.Combine(Here, "a"), Path.Combine(Here, "b"), Path.Combine(Here, "c")], added.Select(p => p.OutputDirectory));
        Assert.Equal(4, options.DevelopmentPlugins.Count);
    }

    [Fact]
    public void ThePublicOverload_ReadsTheProcessEntryAssembly()
    {
        // The test runner's entry assembly has no AlcoveDevelopmentPlugin attributes.
        var options = new PluginHostOptions();

        Assert.Empty(options.AddDevelopmentPluginsFrom([], new Hashtable()));
    }

    [Fact]
    public void DevelopmentPlugins_AreCodeOnly_NeverInTheConfigurationFile()
    {
        var options = new PluginHostOptions { Root = Here };
        options.DevelopmentPlugins.Add(new DevelopmentPlugin(Path.Combine(Here, "bin")));
        var path = Path.Combine(Path.GetTempPath(), $"alcove-options-{Guid.NewGuid():N}.json");
        try
        {
            options.Save(path);

            Assert.DoesNotContain("evelopment", File.ReadAllText(path), StringComparison.Ordinal);
            Assert.Empty(PluginHostOptions.Load(path).DevelopmentPlugins);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ADevelopmentPlugin_NeedsAFolder()
    {
        Assert.Throws<ArgumentException>(() => new DevelopmentPlugin(" "));
        Assert.Null(new DevelopmentPlugin("bin", " ").ProjectPath);
    }

    /// <summary>A dynamic assembly carrying the attributes Alcove.Sdk generates for PluginUnderTest items.</summary>
    private static Assembly AssemblyWith(params (string OutputDirectory, string? ProjectPath)[] plugins)
    {
        var constructor = typeof(AlcoveDevelopmentPluginAttribute).GetConstructor([typeof(string)])!;
        var projectPath = typeof(AlcoveDevelopmentPluginAttribute).GetProperty(nameof(AlcoveDevelopmentPluginAttribute.ProjectPath))!;
        var builder = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName($"Harness{Guid.NewGuid():N}"), AssemblyBuilderAccess.Run);
        foreach (var (outputDirectory, project) in plugins)
        {
            builder.SetCustomAttribute(project is null
                ? new CustomAttributeBuilder(constructor, [outputDirectory])
                : new CustomAttributeBuilder(constructor, [outputDirectory], [projectPath], [project]));
        }

        return builder;
    }
}

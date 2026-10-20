using System.Runtime.Loader;
using Alcove.Wpf.Resources;

namespace Alcove.Wpf.Tests;

/// <summary>Which dictionaries of a plugin assembly are merged, read from real compiled XAML (loaded, never run).</summary>
public sealed class PluginResourceLocatorTests : IDisposable
{
    private readonly TestCells _cells = new();
    private readonly AssemblyLoadContext _context = new("resource-locator", isCollectible: true);

    public void Dispose()
    {
        _context.Unload();
        _cells.Dispose();
    }

    [Fact]
    public void AnAssemblyWithThemesPluginXaml_GetsItMergedByConvention()
        => Assert.Equal(["Themes/Plugin.xaml"], PluginResourceLocator.Find(Load("Fixture.Wpf.Themed")));

    [Fact]
    public void DeclaredDictionaries_ReplaceTheConvention_InTheOrderDeclared()
        => Assert.Equal(["Themes/Colors.xaml", "Themes\\Extra.xaml"], PluginResourceLocator.Find(Load("Fixture.Wpf.Declared")));

    [Fact]
    public void AnAssemblyWithOtherXamlOnly_OrNoXaml_HasNothingToMerge()
    {
        Assert.Empty(PluginResourceLocator.Find(Load("Fixture.Wpf.Controls.V1", "Fixture.Wpf.Controls")));
        Assert.Empty(PluginResourceLocator.Find(Load("Fixture.Wpf.Code")));
    }

    [Theory]
    [InlineData("Badge.xaml", true)]
    [InlineData("/BADGE.xaml", true)]
    [InlineData("Themes/Plugin.xaml", false)]
    public void Parts_AreFoundInTheCompiledXaml_IgnoringCase(string path, bool expected)
        => Assert.Equal(expected, PluginResourceLocator.HasPart(Load("Fixture.Wpf.Controls.V1", "Fixture.Wpf.Controls"), path));

    private System.Reflection.Assembly Load(string project, string? assemblyName = null) =>
        _context.LoadFromAssemblyPath(_cells.Fixture(project, assemblyName));
}

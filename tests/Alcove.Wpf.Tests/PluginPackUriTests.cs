using System.Reflection;
using Alcove.Wpf.Resources;

namespace Alcove.Wpf.Tests;

public sealed class PluginPackUriTests
{
    [Fact]
    public void AnAssemblyWithoutAPublicKey_GetsItsNameAndVersion()
    {
        var name = new AssemblyName("MyPlugin") { Version = new Version(1, 2, 3, 0) };

        Assert.Equal("/MyPlugin;v1.2.3.0;component/Themes/Plugin.xaml", PluginPackUri.Format(name, "Themes/Plugin.xaml"));
    }

    [Fact]
    public void AStrongNamedAssembly_AlsoGetsItsPublicKeyToken()
    {
        var assembly = typeof(object).Assembly;
        var version = assembly.GetName().Version;

        Assert.Equal($"/System.Private.CoreLib;v{version};7cec85d7bea7798e;component/a.xaml", PluginPackUri.Format(assembly, "a.xaml"));
    }

    [Theory]
    [InlineData("/Themes/Plugin.xaml")]
    [InlineData("Themes\\Plugin.xaml")]
    [InlineData("\\Themes/Plugin.xaml")]
    public void ThePathIsNormalised(string path)
    {
        var name = new AssemblyName("P") { Version = new Version(2, 0) };

        Assert.Equal("/P;v2.0;component/Themes/Plugin.xaml", PluginPackUri.Format(name, path));
    }

    [Fact]
    public void ANameWithoutAVersion_CantBeVersioned()
        => Assert.Equal("/P;component/x.xaml", PluginPackUri.Format(new AssemblyName("P"), "x.xaml"));

    [Fact]
    public void Create_GivesARelativeUri()
    {
        var uri = PluginPackUri.Create(typeof(PluginPackUriTests).Assembly, "Views/Main.xaml");

        Assert.False(uri.IsAbsoluteUri);
        Assert.Equal($"/Alcove.Wpf.Tests;v{typeof(PluginPackUriTests).Assembly.GetName().Version};component/Views/Main.xaml", uri.OriginalString);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("/")]
    public void AnEmptyPath_IsRejected(string path)
        => Assert.Throws<ArgumentException>(() => PluginPackUri.Format(new AssemblyName("P"), path));
}

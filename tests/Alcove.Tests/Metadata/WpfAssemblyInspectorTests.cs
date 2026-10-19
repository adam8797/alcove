using Alcove.Internal.Metadata;

namespace Alcove.Tests.Metadata;

/// <summary>
/// The WPF metadata inspector against real WPF fixture libraries (net10.0-windows, built on any OS):
/// Fixture.Wpf.Code (no XAML) and three builds of the XAML library Fixture.Wpf.Controls.
/// </summary>
public sealed class WpfAssemblyInspectorTests
{
    [Fact]
    public void Inspect_XamlLibrary_HasWpfResourcesAndIdentity()
    {
        var info = Inspect(MetadataFixtures.WpfControlsV1);

        Assert.True(info.HasWpfResources);
        Assert.Equal(new AssemblyIdentity("Fixture.Wpf.Controls", new Version(1, 0, 0, 0), null), info.Identity);
        Assert.Equal(MetadataFixtures.WpfControlsV1, info.Path);
    }

    [Fact]
    public void Inspect_LibraryWithoutXaml_HasNoWpfResources()
    {
        var info = Inspect(MetadataFixtures.WpfCode);

        Assert.False(info.HasWpfResources);
        Assert.Empty(info.BamlAssemblyReferences);
        Assert.Empty(info.UnversionedLocators);
    }

    [Fact]
    public void Inspect_VersionPair_DiffersOnlyInAssemblyVersion()
    {
        var v1 = Inspect(MetadataFixtures.WpfControlsV1);
        var v2 = Inspect(MetadataFixtures.WpfControlsV2);

        Assert.Equal(v1.Name, v2.Name);
        Assert.Equal(new Version(2, 0, 0, 0), v2.Identity.Version);
        Assert.NotEqual(v1.Identity, v2.Identity);
    }

    [Fact]
    public void Inspect_ListsAssemblyReferences()
    {
        var info = Inspect(MetadataFixtures.WpfControlsV1);

        Assert.Contains(info.AssemblyReferences, r => r.Name == "PresentationFramework" && r.PublicKeyToken == "31bf3856ad364e35");
        Assert.Contains(info.AssemblyReferences, r => r.Name == "System.Runtime" && r.PublicKeyToken == "b03f5f7f11d50a3a");
    }

    [Fact]
    public void Inspect_ReadsBamlAssemblyRecords()
    {
        var info = Inspect(MetadataFixtures.WpfControlsV1);

        // Badge.xaml uses Fixture.Wpf.Code from XAML only, so the compiler leaves it out of the AssemblyRef table,
        // but WPF still resolves it by name: this is why relevance also reads BAML.
        Assert.DoesNotContain(info.AssemblyReferences, r => r.Name == "Fixture.Wpf.Code");
        Assert.Contains(info.BamlAssemblyReferences, r => r.Name == "Fixture.Wpf.Code" && r.Version == new Version(1, 0, 0, 0));
        Assert.Contains(info.BamlAssemblyReferences, r => r.Name == "PresentationFramework");
    }

    [Fact]
    public void Inspect_ExplicitAssemblyVersion_HasNoUnversionedLocators()
    {
        Assert.Empty(Inspect(MetadataFixtures.WpfControlsV1).UnversionedLocators);
        Assert.Empty(Inspect(MetadataFixtures.WpfControlsV2).UnversionedLocators);
    }

    [Fact]
    public void Inspect_WithoutExplicitAssemblyVersion_FindsUnversionedLocators()
    {
        var info = Inspect(MetadataFixtures.WpfControlsUnversioned);

        Assert.Equal(new Version(3, 0, 0, 0), info.Identity.Version);
        Assert.Equal(["/Fixture.Wpf.Controls;component/badge.xaml"], info.UnversionedLocators);
        Assert.True(info.HasUnversionedLocators);
    }

    [Fact]
    public void Inspect_NonManagedFile_ReturnsNull()
    {
        var path = Path.Combine(Path.GetTempPath(), $"alcove-native-{Guid.NewGuid():N}.dll");
        File.WriteAllBytes(path, new byte[256]);
        try
        {
            Assert.Null(WpfAssemblyInspector.Inspect(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Inspect_StrongNamedAssembly_ComputesTheToken()
    {
        var info = Inspect(typeof(object).Assembly.Location);

        Assert.Equal("7cec85d7bea7798e", info.Identity.PublicKeyToken);
        Assert.False(info.HasWpfResources);
    }

    [Theory]
    [InlineData("/Lib;component/view.xaml", true)]
    [InlineData("pack://application:,,,/Lib;component/Themes/Generic.xaml", true)]
    [InlineData("/lib;COMPONENT/view.xaml", true)]
    [InlineData("/Lib;31bf3856ad364e35;component/view.xaml", true)]
    [InlineData("/Lib;V1.0.0.0;component/view.xaml", false)]
    [InlineData("/Lib;v2.0.0.0;31bf3856ad364e35;component/view.xaml", false)]
    [InlineData("/Other;component/view.xaml", false)]
    [InlineData("/LibExtra;component/view.xaml", false)]
    [InlineData("/Lib;V1.0.0.0;component/a.xaml and /Lib;component/b.xaml", true)]
    public void IsUnversionedLocator(string value, bool expected)
    {
        Assert.Equal(expected, WpfAssemblyInspector.IsUnversionedLocator(value, "Lib"));
    }

    [Fact]
    public void Inspect_IsFast()
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        Inspect(MetadataFixtures.WpfControlsUnversioned);
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"Took {stopwatch.Elapsed.TotalMilliseconds:F0} ms.");
    }

    internal static WpfAssemblyInfo Inspect(string path) =>
        WpfAssemblyInspector.Inspect(path) ?? throw new InvalidOperationException($"{path} isn't an assembly.");
}

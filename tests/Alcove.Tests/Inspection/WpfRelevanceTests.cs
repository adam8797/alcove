using Alcove.Inspection;

namespace Alcove.Tests.Inspection;

public sealed class WpfRelevanceTests
{
    [Fact]
    public void FindRelevant_XamlAssemblyAndWhatItReferences()
    {
        var controls = WpfAssemblyInspectorTests.Inspect(MetadataFixtures.WpfControlsV1);
        var code = WpfAssemblyInspectorTests.Inspect(MetadataFixtures.WpfCode);
        var unrelated = WpfAssemblyInspectorTests.Inspect(MetadataFixtures.Path("Fixture.Leak.Dependency"));

        var relevant = WpfRelevance.FindRelevant([controls, code, unrelated]);

        Assert.Equal(["Fixture.Wpf.Code", "Fixture.Wpf.Controls"], relevant.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void FindRelevant_ReferencedOnlyFromAnotherContext_IsNotRelevant()
    {
        var code = WpfAssemblyInspectorTests.Inspect(MetadataFixtures.WpfCode);

        Assert.Empty(WpfRelevance.FindRelevant([code]));
    }

    [Fact]
    public void FindRelevant_BamlOnlyReference_Counts()
    {
        var code = Info("Lib", hasXaml: false);
        var view = Info("View", hasXaml: true) with { BamlAssemblyReferences = [code.Identity] };

        Assert.Equal(["Lib", "View"], WpfRelevance.FindRelevant([view, code]).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void FindRelevant_ReferenceFromNonXamlAssembly_DoesNotCount()
    {
        var lib = Info("Lib", hasXaml: false);
        var user = Info("User", hasXaml: false) with { AssemblyReferences = [lib.Identity] };

        Assert.Empty(WpfRelevance.FindRelevant([user, lib]));
    }

    internal static WpfAssemblyInfo Info(string name, bool hasXaml, string version = "1.0.0.0", string? token = null, bool unversionedLocators = false) =>
        new(
            $"/packages/{name}/{version}/{name}.dll",
            new AssemblyIdentity(name, Version.Parse(version), token),
            hasXaml,
            [],
            [],
            unversionedLocators ? [$"/{name};component/view.xaml"] : []);
}

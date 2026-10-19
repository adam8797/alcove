using Alcove.IntegrationTests.Infrastructure;

namespace Alcove.Wpf.Tests.Windows;

/// <summary>
/// Alcove.Wpf with a running WPF (Windows only), from the spike's scenarios (spike/wpf-across-load-contexts): each runs
/// the WPF test host in a process of its own, against real plugin packages, rendering off-screen.
/// </summary>
public sealed class WpfScenarioTests(WpfFixtures fixtures) : IDisposable
{
    private const string View = "Fixture.Wpf.View";

    private readonly TempDirectory _directory = new("alcove-wpf");

    public void Dispose() => _directory.DisposeBestEffort();

    [Fact]
    public async Task APluginUserControl_RendersFromItsCell_WithItsDictionaryMergedAndRemoved_AndItsViewModelsView()
    {
        SkipUnlessWindows();
        var root = await fixtures.PrepareRootAsync(_directory, View);

        var result = await fixtures.RunAsync("render", root);

        result.ExpectContains("loaded", "Loaded");
        result.Expect("cell", View);
        result.Expect("merged", "true");
        result.Expect("view.text", "Hello from a cell");
        result.Expect("view.drawn", "true");
        result.Expect("background", "#FF336699");   // StaticResource from the plugin's Themes/Plugin.xaml, merged before it loaded
        result.Expect("accent", "#FF008000");       // DynamicResource to a host SharedResourceKey
        result.Expect("viewmodelview", "GreetingView");
        result.Expect("viewmodel.text", "Greetings from a view-model");
        result.Expect("unmerged", "true");
        Assert.DoesNotContain("LOG Information 6009", result.Output);   // the test host sets the AppContext switch
    }

    [Fact]
    public async Task AControlLibraryUsedOnlyFromXaml_RendersWithPreloading_AndFailsWithout()
    {
        SkipUnlessWindows();
        var with = await fixtures.RunAsync("xamlonly", await fixtures.PrepareRootAsync(_directory, "Fixture.Wpf.XamlOnly"));
        using var other = new TempDirectory("alcove-wpf");
        var without = await fixtures.RunAsync("xamlonly", await fixtures.PrepareRootAsync(other, "Fixture.Wpf.XamlOnly"), "--no-preload");

        with.Expect("view.text", "XamlOnly plugin|XamlLib control");
        with.Expect("view.drawn", "true");
        without.ExpectContains("error", "Fixture.Wpf.XamlLib");
        other.DisposeBestEffort();
    }

    [Fact]
    public async Task AXamlLibraryRolledForwardInItsCell_RendersWithTheForwarder_AndFailsWithout()
    {
        // RollOld is built against RollLib 1.0, RollNew against 2.0; sharing cell 'Roll', both get 2.0. RollOld's BAML
        // asks for 1.0.0.0, which no loaded copy has, so WPF's Assembly.Load goes to the default context.
        SkipUnlessWindows();
        var with = await fixtures.RunAsync("rollforward", await fixtures.PrepareRootAsync(_directory, "Fixture.Wpf.RollOld", "Fixture.Wpf.RollNew"), "--forward");
        using var other = new TempDirectory("alcove-wpf");
        var without = await fixtures.RunAsync("rollforward", await fixtures.PrepareRootAsync(other, "Fixture.Wpf.RollOld", "Fixture.Wpf.RollNew"));

        with.ExpectContains("loaded.Fixture.Wpf.RollOld", "Loaded");
        with.ExpectContains("loaded.Fixture.Wpf.RollNew", "Loaded");
        with.Expect("view.text", "Built against RollLib 1|RollLib 2");
        Assert.Contains("LOG Debug 6002", with.Output);
        without.ExpectContains("error", "Fixture.Wpf.RollLib");
        other.DisposeBestEffort();
    }

    [Fact]
    public async Task TwoPluginsInSeparateCells_ShareAnIdenticalControlLibrary_AndBothRender()
    {
        SkipUnlessWindows();
        var result = await fixtures.RunAsync("shared", await fixtures.PrepareRootAsync(_directory, "Fixture.Wpf.SharedUserA", "Fixture.Wpf.SharedUserB"));

        result.Expect("SharedUserA.text", "User A|Shared badge");
        result.Expect("SharedUserB.text", "User B|Shared badge");
        result.Expect("SharedUserA.cell", "Fixture.Wpf.SharedUserA");
        result.Expect("SharedUserB.cell", "Fixture.Wpf.SharedUserB");
        result.Expect("badges", "SharedBadge,SharedBadge");
        result.Expect("onecopy", "true");
    }

    [Fact]
    public async Task AHotInstall_LoadsAPluginWithItsResources_AndTheDispatcherCatalogDeliversItOnTheUiThread()
    {
        SkipUnlessWindows();
        var result = await fixtures.RunAsync("hotinstall", _directory.Combine("root"));

        result.Expect("status", "Loaded");
        result.Expect("observed", "Hello");
        result.Expect("observedonui", "true");
        result.Expect("addedonui", "true");
        result.Expect("merged", "true");
        result.Expect("view.text", "Hello from a cell");
        result.Expect("unmerged", "true");
    }

    [Fact]
    public async Task AnInstallOnAnotherThread_MergesThroughTheDispatcher_AndCallbacksAreMarshalledToTheUiThread()
    {
        SkipUnlessWindows();
        var result = await fixtures.RunAsync("dispatcher", _directory.Combine("root"));

        result.Expect("status", "Loaded");
        result.Expect("rawonui", "false");   // the catalog itself raised it on the installing thread
        result.Expect("observedonui", "true");
        result.Expect("addedonui", "true");
        result.Expect("merged", "true");
        result.Expect("view.text", "Hello from a cell");
    }

    private static void SkipUnlessWindows() => Assert.SkipWhen(WpfFixtures.SkipReason is not null, WpfFixtures.SkipReason ?? "");
}

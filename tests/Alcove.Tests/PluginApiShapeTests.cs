using Alcove.Catalog;
using Alcove.Cells;
using Alcove.Feeds;
using Alcove.Hosting;
using Alcove.Lifecycle;
using Alcove.Tests.TestSupport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NuGet.Versioning;

namespace Alcove.Tests;

/// <summary>
/// The samples in docs/plugin-developer-guide.md and docs/app-developer-guide.md, compiled against the real
/// API: if a shape changes, this file stops compiling.
/// </summary>
public sealed class PluginApiShapeTests
{
    [Fact]
    public void EntryPointSample_RegistersWithFactoriesTypesMetadataAndLifetimes()
    {
        var registrar = new RecordingRegistrar();

        new ChartsPlugin().Register(registrar, new FakeContext());

        Assert.Collection(registrar.Registrations,
            r =>
            {
                Assert.Equal(typeof(IToolWindow), r.ExtensionPoint);
                Assert.Equal("Charts", Assert.IsType<ToolWindowAttribute>(r.Metadata).Title);
                Assert.Equal(ExtensionLifetime.Singleton, r.Lifetime);
            },
            r => Assert.Equal(ExtensionLifetime.Transient, r.Lifetime),
            r =>
            {
                Assert.Equal(typeof(ChartWindow), r.Implementation);
                Assert.Null(r.Metadata);
            });
    }

    [Fact]
    public async Task InitializeAsync_DefaultImplementation_CompletesSynchronously()
    {
        IPlugin plugin = new MinimalPlugin();

        var task = plugin.InitializeAsync(new FakeContext(), TestContext.Current.CancellationToken);

        Assert.True(task.IsCompletedSuccessfully);
        await task;
    }

    [Fact]
    public void ExtensionAttribute_SubclassInstanceIsTheMetadata()
    {
        var attribute = (ToolWindowAttribute)Attribute.GetCustomAttribute(typeof(ChartWindow), typeof(ExtensionAttribute))!;

        Assert.Equal(typeof(IToolWindow), attribute.ExtensionPoint);
        Assert.Equal("Charts", attribute.Title);
        Assert.Equal(ExtensionLifetime.Transient, attribute.Lifetime);
    }

    [Fact]
    public void ExtensionAttribute_DefaultsToSingleton()
        => Assert.Equal(ExtensionLifetime.Singleton, new ExtensionAttribute(typeof(IToolWindow)).Lifetime);

    [Fact]
    public void ExtensionAttribute_AllowsSeveralPerTypeAndIsDeclaredNotInherited()
    {
        var usage = (AttributeUsageAttribute)Attribute.GetCustomAttribute(typeof(ExtensionAttribute), typeof(AttributeUsageAttribute))!;

        Assert.Equal(AttributeTargets.Class, usage.ValidOn);
        Assert.True(usage.AllowMultiple);
        Assert.False(usage.Inherited);
        Assert.Equal(2, typeof(TwoWayWindow).GetCustomAttributes(typeof(ExtensionAttribute), inherit: false).Length);
        // The runtime doesn't apply an inherited AttributeUsage's Inherited = false to subclasses such as
        // ToolWindowAttribute, so the scanner must read declared attributes only (inherit: false).
        Assert.Empty(typeof(DerivedWindow).GetCustomAttributes(typeof(ExtensionAttribute), inherit: false));
    }

    [Fact]
    public void ExtensionAttribute_RejectsNullExtensionPoint()
        => Assert.Throws<ArgumentNullException>(() => new ExtensionAttribute(null!));

    [Fact]
    public void ExtensionLifetime_SingletonIsTheDefaultValue()
        => Assert.Equal(ExtensionLifetime.Singleton, default(ExtensionLifetime));

    // --- docs/app-developer-guide.md §2, §3 and §5 ---
    // Not yet: PluginHostOptions.TrustPolicy (Phase 4) and host.RestoreAsync (Phase 5).
    [Fact]
    public async Task AppGuideSamples_CreateValidateLoadAndUseTheCatalog()
    {
        using var temp = new TempDirectory();
        var ct = TestContext.Current.CancellationToken;
        ILoggerFactory loggerFactory = NullLoggerFactory.Instance;
        var log = loggerFactory.CreateLogger("app");

        // §2, entirely in code (with a temporary root instead of LocalApplicationData).
        await using var host = PluginHost.Create(new PluginHostOptions
        {
            Root = temp.Combine("MyApp", "plugins"),
            PluginPackageType = "MyAppPlugin",
            HostOwnedPackages = { "MyApp.Contracts", "MyApp.Sdk" },
            Feeds = { new PackageFeed("https://nuget.example.com/v3/index.json") },
            LoggerFactory = loggerFactory,
        });

        // Optional: check what validation thinks before loading.
        var report = await host.ValidateAsync(ct);

        // Always validates first; then loads every enabled plugin.
        await host.LoadAsync(ct);

        foreach (var plugin in host.Plugins.Where(p => p.Status == PluginStatus.Faulted))
            log.LogError(plugin.Fault, "Plugin {Id} failed to load", plugin.Id);

        // §3
        var menu = new List<(string Title, Action Open)>();
        foreach (var ext in host.Catalog.GetAll<IToolWindow, ToolWindowAttribute>())
            menu.Add((ext.Metadata.Title, () => Show(ext.GetInstance())));   // metadata without instantiation

        // Existing extensions, then any added later by a hot install:
        using var subscription = host.Catalog.Observe<IToolWindow>(ext => menu.Add(("?", () => Show(ext.GetInstance()))));

        // §5
        var options = new PluginHostOptions { Root = temp.Path };
        options.Cells.Add(new CellDefinition("Reporting")
        {
            Plugins = { "Contoso.Charts", "Contoso.Tables" }
        });

        Assert.Empty(report.Issues);
        Assert.Empty(host.Plugins);
        Assert.Empty(menu);
        Assert.Equal(["Contoso.Charts", "Contoso.Tables"], options.Cells[0].Plugins);

        static void Show(IToolWindow window) => window.CreateContent();
    }

    [Fact]
    public async Task AppGuideSamples_ManagePlugins()
    {
        using var temp = new TempDirectory();
        var ct = TestContext.Current.CancellationToken;
        await using var host = PluginHost.Create(new PluginHostOptions
        {
            Root = temp.Path,
            Feeds = { new PackageFeed("https://nuget.example.com/v3/index.json") },
        });

        // §4, the calls that need no feed on an empty root (nothing installed).
        var updates = await host.CheckForUpdatesAsync(ct);
        var updated = await host.UpdateAsync("Contoso.Charts", ct);              // staged until restart
        var uninstalled = await host.UninstallAsync("Contoso.Charts", cascade: false, ct);
        var disabled = await host.DisableAsync("Contoso.Charts", ct);             // staged until restart
        var rolledBack = await host.RollbackAsync("Contoso.Charts", ct);            // staged until restart
        var pending = await host.GetPendingChangesAsync(ct);       // includes changes from other instances

        Assert.Empty(updates);
        Assert.All([updated, uninstalled, disabled, rolledBack], r => Assert.Equal(ChangeStatus.Failed, r.Status));
        Assert.Contains("isn't installed", updated.Reason);
        Assert.Empty(pending);
        Assert.True((await host.RestoreAsync(ct)).Succeeded);
        Assert.False((await host.VerifyAsync(ct)).HasErrors);
    }

    [Fact]
    public void PluginHostCreate_NeedsAnAbsoluteRoot()
    {
        Assert.Throws<ArgumentException>(() => PluginHost.Create(new PluginHostOptions()));
        Assert.Throws<ArgumentException>(() => PluginHost.Create(new PluginHostOptions { Root = "relative/plugins" }));
    }

    [Fact]
    public void PluginHostCreate_RejectsASingleFileHost()
    {
        // A single-file host exposes no deps files (APP_CONTEXT_DEPS_FILES is empty).
        var singleFile = new Alcove.Hosting.HostEnvironment(DepsFiles: null, EntryAssembly: null, "linux-x64");

        var ex = Assert.Throws<UnsupportedHostException>(() => PluginHost.Create(new PluginHostOptions { Root = Path.GetTempPath() }, singleFile));
        Assert.IsAssignableFrom<NotSupportedException>(ex);
    }

    // --- docs/app-developer-guide.md §1 ---
    public interface IToolWindow
    {
        object CreateContent();
    }

    public sealed class ToolWindowAttribute : ExtensionAttribute
    {
        public ToolWindowAttribute() : base(typeof(IToolWindow)) { }
        public required string Title { get; init; }
        public string? Icon { get; init; }
    }

    // --- docs/plugin-developer-guide.md §2 ---
    public sealed class ChartsPlugin : IPlugin
    {
        public void Register(IExtensionRegistrar registrar, IPluginContext context)
        {
            registrar.Add<IToolWindow>(() => new ChartWindow(),
                new ToolWindowAttribute { Title = "Charts" });
            registrar.Add<IToolWindow>(() => new ChartWindow(),
                new ToolWindowAttribute { Title = "Charts" }, ExtensionLifetime.Transient);
            registrar.Add<IToolWindow, ChartWindow>();
        }

        public async ValueTask InitializeAsync(IPluginContext context, CancellationToken ct)
            => await Task.Delay(0, ct);
    }

    [ToolWindow(Title = "Charts", Lifetime = ExtensionLifetime.Transient)]
    public class ChartWindow : IToolWindow
    {
        public object CreateContent() => "chart";
    }

    public sealed class DerivedWindow : ChartWindow;

    [Extension(typeof(IToolWindow))]
    [ToolWindow(Title = "Second")]
    public sealed class TwoWayWindow : IToolWindow
    {
        public object CreateContent() => "two";
    }

    private sealed class MinimalPlugin : IPlugin
    {
        public void Register(IExtensionRegistrar registrar, IPluginContext context) { }
    }

    private sealed record Registration(Type ExtensionPoint, Type? Implementation, object? Metadata, ExtensionLifetime Lifetime);

    private sealed class RecordingRegistrar : IExtensionRegistrar
    {
        public List<Registration> Registrations { get; } = [];

        public void Add<T>(Func<T> factory, object? metadata = null, ExtensionLifetime lifetime = ExtensionLifetime.Singleton)
            where T : class
            => Registrations.Add(new(typeof(T), null, metadata, lifetime));

        public void Add<T, TImplementation>(object? metadata = null, ExtensionLifetime lifetime = ExtensionLifetime.Singleton)
            where T : class
            where TImplementation : class, T
            => Registrations.Add(new(typeof(T), typeof(TImplementation), metadata, lifetime));
    }

    private sealed class FakeContext : IPluginContext
    {
        public string PluginId => "Contoso.Charts";
        public NuGetVersion Version { get; } = NuGetVersion.Parse("1.0.0");
        public string DataDirectory => "/data/Contoso.Charts";
        public ILogger Logger => NullLogger.Instance;
        public IServiceProvider Services { get; } = new EmptyServices();

        private sealed class EmptyServices : IServiceProvider
        {
            public object? GetService(Type serviceType) => null;
        }
    }
}

using System.Runtime.Loader;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Alcove;
using Alcove.DependencyInjection;
using Alcove.Wpf;
using Fixture.Wpf.HostSdk;
using Microsoft.Extensions.DependencyInjection;

namespace Alcove.Wpf.TestHost;

/// <summary>
/// Runs one Alcove.Wpf scenario and prints its observations as <c>CHECK name=value</c> lines for the test to assert on.
/// </summary>
/// <remarks>
/// <c>prepare --root R --feed F --install A,B</c> installs plugins into a fresh plugin root (no WPF set-up; nothing is
/// shown), so a later run loads them at startup. <c>run &lt;scenario&gt; --root R [--feed F] [--no-preload] [--forward]</c>
/// runs a scenario on this STA thread's dispatcher, with an <see cref="Application"/> that never shows a window: views
/// are measured, arranged and rendered off-screen with <see cref="RenderTargetBitmap"/>. A watchdog ends the process
/// after <see cref="Timeout"/>. Exit code 0: the scenario ran (its checks may still describe failures the test expects).
/// </remarks>
internal static class Program
{
    private const string PackageType = "FixtureWpfPlugin";
    private const string HostSdk = "Fixture.Wpf.HostSdk";
    private const string ViewPlugin = "Fixture.Wpf.View";
    private const string BackgroundKey = "Fixture.Wpf.View.Background";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    [STAThread]
    private static int Main(string[] args)
    {
        using var watchdog = new Timer(_ =>
        {
            Console.WriteLine($"TIMEOUT after {Timeout}");
            Console.Out.Flush();
            Environment.Exit(3);
        }, null, Timeout, System.Threading.Timeout.InfiniteTimeSpan);

        try
        {
            var options = Arguments.Parse(args);
            System.IO.Directory.CreateDirectory(options.Root);
            return options.Command switch
            {
                "prepare" => PrepareAsync(options).GetAwaiter().GetResult(),
                "run" => Run(options),
                _ => Usage($"Unknown command '{options.Command}'."),
            };
        }
        catch (ArgumentException ex)
        {
            return Usage(ex.Message);
        }
    }

    private static int Usage(string error)
    {
        Console.WriteLine($"ERROR {error}");
        Console.WriteLine("Usage: prepare --root R --feed F --install A,B | run <scenario> --root R [--feed F] [--no-preload] [--forward]");
        return 2;
    }

    private static async Task<int> PrepareAsync(Arguments arguments)
    {
        await using var host = PluginHost.Create(Options(arguments));
        await host.LoadAsync();
        var ok = true;
        foreach (var id in arguments.Install)
        {
            var result = await host.InstallAsync(id);
            Console.WriteLine($"INSTALLED {id}: {result.Status}{(result.Reason is { Length: > 0 } reason ? $" ({reason})" : "")}");
            ok &= result.IsInstalled;
        }

        return ok ? 0 : 1;
    }

    private static int Run(Arguments arguments)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var exitCode = 1;
        app.Startup += async (_, _) =>
        {
            try
            {
                await RunScenarioAsync(app, arguments);
                exitCode = 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR {Describe(ex)}");
                Console.WriteLine(ex);
            }
            finally
            {
                app.Shutdown();
            }
        };
        app.DispatcherUnhandledException += (_, e) =>
        {
            Console.WriteLine($"ERROR unhandled on the dispatcher: {Describe(e.Exception)}");
            e.Handled = true;
        };
        app.Run();
        Console.Out.Flush();
        return exitCode;
    }

    private static Task RunScenarioAsync(Application app, Arguments arguments) => arguments.Scenario switch
    {
        "render" => RenderAsync(app, arguments),
        "xamlonly" => SingleViewAsync(app, arguments, "XamlOnly"),
        "rollforward" => SingleViewAsync(app, arguments, "RollOld"),
        "shared" => SharedAsync(app, arguments),
        "hotinstall" => HotInstallAsync(app, arguments, fromThreadPool: false),
        "dispatcher" => HotInstallAsync(app, arguments, fromThreadPool: true),
        _ => throw new ArgumentException($"Unknown scenario '{arguments.Scenario}'."),
    };

    /// <summary>
    /// A plugin UserControl from a cell, its merged dictionary (and its removal), a host key, and a view-model's view;
    /// with Alcove.DependencyInjection's AddPlugins, configured after UseWpf.
    /// </summary>
    private static async Task RenderAsync(Application app, Arguments arguments)
    {
        app.Resources.SetResource(HostKeys.Accent, Brushes.Green);
        var services = new ServiceCollection()
            .AddPlugins(options =>
            {
                var configured = Options(arguments);
                options.Root = configured.Root;
                options.PluginPackageType = configured.PluginPackageType;
                options.HostOwnedPackages.Add(HostSdk);
                options.LoggerFactory = configured.LoggerFactory;
                options.UseWpf(app, wpf =>
                {
                    wpf.PreloadWpfAssemblies = !arguments.NoPreload;
                    wpf.MapViewModelsToViews = true;
                });
            })
            .BuildServiceProvider();
        var host = services.GetRequiredService<PluginHost>();
        await host.LoadAsync();
        Check("loaded", Status(host, ViewPlugin));
        Check("merged", app.TryFindResource(BackgroundKey) is not null);

        var view = host.Catalog.GetAll<IViewFactory>().Single(e => e.Value.Name == "Hello").Value.CreateView();
        Check("cell", AssemblyLoadContext.GetLoadContext(view.GetType().Assembly)?.Name);
        await RenderAsync(view, "view");
        Check("background", (FindVisuals<Border>(view).FirstOrDefault()?.Background as SolidColorBrush)?.Color.ToString());
        Check("accent", (FindVisuals<TextBlock>(view).FirstOrDefault()?.Foreground as SolidColorBrush)?.Color.ToString());

        var viewModel = host.Catalog.GetAll<IViewModelFactory>().Single().Value.CreateViewModel();
        var presenter = new ContentControl { Content = viewModel };
        await RenderAsync(presenter, "viewmodel");
        Check("viewmodelview", FindVisuals<UserControl>(presenter).FirstOrDefault()?.GetType().Name);

        await services.DisposeAsync();   // disposes the host
        Check("unmerged", app.TryFindResource(BackgroundKey) is null);
    }

    /// <summary>One plugin view, rendered (or the exception creating it).</summary>
    private static async Task SingleViewAsync(Application app, Arguments arguments, string name)
    {
        await using var host = Create(app, arguments);
        await host.LoadAsync();
        foreach (var plugin in host.Plugins)
        {
            Check($"loaded.{plugin.Id}", plugin.Status);
        }

        try
        {
            var view = host.Catalog.GetAll<IViewFactory>().Single(e => e.Value.Name == name).Value.CreateView();
            await RenderAsync(view, "view");
        }
        catch (Exception ex)
        {
            Check("error", Describe(ex));
        }
    }

    /// <summary>Two plugins in separate cells using one identical XAML control library (ADR 0015).</summary>
    private static async Task SharedAsync(Application app, Arguments arguments)
    {
        await using var host = Create(app, arguments);
        await host.LoadAsync();
        var badges = new List<Type>();
        foreach (var name in new[] { "SharedUserA", "SharedUserB" })
        {
            try
            {
                var view = host.Catalog.GetAll<IViewFactory>().Single(e => e.Value.Name == name).Value.CreateView();
                await RenderAsync(view, name);
                Check($"{name}.cell", AssemblyLoadContext.GetLoadContext(view.GetType().Assembly)?.Name);
                badges.AddRange(FindVisuals<UserControl>(view).Skip(1).Take(1).Select(b => b.GetType()));
            }
            catch (Exception ex)
            {
                Check($"{name}.error", Describe(ex));
            }
        }

        Check("badges", string.Join(",", badges.Select(b => b.Name)));
        Check("onecopy", badges.Count == 2 && badges[0].Assembly == badges[1].Assembly);
    }

    /// <summary>A hot install under UseWpf, with the catalog's callbacks on the dispatcher.</summary>
    private static async Task HotInstallAsync(Application app, Arguments arguments, bool fromThreadPool)
    {
        var ui = Environment.CurrentManagedThreadId;
        var host = Create(app, arguments);
        var catalog = host.Catalog.OnDispatcher(app.Dispatcher);
        var observed = new TaskCompletionSource<Extension<IViewFactory>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var addedOnUi = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rawThread = 0;
        host.Catalog.ExtensionsAdded += (_, _) => rawThread = Environment.CurrentManagedThreadId;
        catalog.ExtensionsAdded += (_, _) => addedOnUi.TrySetResult(Environment.CurrentManagedThreadId == ui);
        using var subscription = catalog.Observe<IViewFactory>(e =>
        {
            Check("observedonui", Environment.CurrentManagedThreadId == ui);
            observed.TrySetResult(e);
        });
        await host.LoadAsync();

        var result = fromThreadPool
            ? await Task.Run(() => host.InstallAsync(ViewPlugin))
            : await host.InstallAsync(ViewPlugin);
        Check("status", result.Status);
        Check("rawonui", rawThread == ui);

        var extension = await observed.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Check("observed", extension.Value.Name);
        Check("addedonui", await addedOnUi.Task.WaitAsync(TimeSpan.FromSeconds(30)));
        Check("merged", app.TryFindResource(BackgroundKey) is not null);
        await RenderAsync(extension.Value.CreateView(), "view");

        await host.DisposeAsync();
        Check("unmerged", app.TryFindResource(BackgroundKey) is null);
    }

    private static PluginHost Create(Application app, Arguments arguments, Action<WpfPluginOptions>? configure = null)
    {
        var options = Options(arguments);
        options.UseWpf(app, wpf =>
        {
            wpf.PreloadWpfAssemblies = !arguments.NoPreload;
            wpf.ForwardDefaultContextLoads = arguments.Forward;
            configure?.Invoke(wpf);
        });
        return PluginHost.Create(options);
    }

    private static PluginHostOptions Options(Arguments arguments)
    {
        var options = new PluginHostOptions
        {
            Root = arguments.Root,
            PluginPackageType = PackageType,
            HostOwnedPackages = { HostSdk },
            LoggerFactory = new ConsoleLoggerFactory(),
        };
        if (arguments.Feed is { } feed)
        {
            options.Feeds.Add(new PackageFeed(feed));
        }

        return options;
    }

    /// <summary>Lays out and renders an element off-screen, then reports its text and whether anything was drawn.</summary>
    private static async Task RenderAsync(FrameworkElement element, string name)
    {
        var size = new Size(400, 200);
        element.Measure(size);
        element.Arrange(new Rect(size));
        element.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);   // let bindings and dynamic resources settle

        var bitmap = new RenderTargetBitmap(400, 200, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var pixels = new byte[400 * 200 * 4];
        bitmap.CopyPixels(pixels, 400 * 4, 0);
        var drawn = 0;
        for (var i = 3; i < pixels.Length; i += 4)
        {
            drawn += pixels[i] != 0 ? 1 : 0;
        }

        Check($"{name}.text", string.Join("|", FindVisuals<TextBlock>(element).Select(t => t.Text)));
        Check($"{name}.drawn", drawn > 0);
    }

    private static IEnumerable<T> FindVisuals<T>(DependencyObject root)
        where T : DependencyObject
    {
        if (root is T match)
        {
            yield return match;
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            foreach (var child in FindVisuals<T>(VisualTreeHelper.GetChild(root, i)))
            {
                yield return child;
            }
        }
    }

    private static string Status(PluginHost host, string id) =>
        host.Plugins.SingleOrDefault(p => p.Id == id)?.ToString() ?? "missing";

    private static void Check(string name, object? value) =>
        Console.WriteLine($"CHECK {name}={value switch { bool b => b ? "true" : "false", null => "(null)", _ => value }}");

    private static string Describe(Exception ex)
    {
        var text = $"{ex.GetType().Name}: {ex.Message.Split('\n')[0].Trim()}";
        for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
        {
            text += $" ---> {inner.GetType().Name}: {inner.Message.Split('\n')[0].Trim()}";
        }

        return text;
    }
}

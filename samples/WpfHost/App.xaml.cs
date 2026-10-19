using System.IO;
using System.Windows;
using Alcove;
using Alcove.Wpf;

namespace WpfHost;

/// <summary>
/// The Alcove WPF sample. Usage: <c>WpfHost &lt;plugin-root&gt; [--feed &lt;folder&gt;] [--install Id1,Id2]</c>. Loads the plugins
/// installed in the root; <c>--install</c> installs plugins from the feed at startup when the root has none yet. The window
/// lists the feed's other plugins and installs the selected one while running (hot install).
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;
        if (args.Length == 0)
        {
            MessageBox.Show("Usage: WpfHost <plugin-root> [--feed <folder>] [--install Id1,Id2]", "Alcove WPF sample");
            Shutdown(2);
            return;
        }

        var root = Path.GetFullPath(args[0]);
        string? feed = null;
        string[] install = [];
        for (var i = 1; i + 1 < args.Length; i += 2)
        {
            switch (args[i])
            {
                case "--feed": feed = Path.GetFullPath(args[i + 1]); break;
                case "--install": install = args[i + 1].Split(',', StringSplitOptions.RemoveEmptyEntries); break;
            }
        }

        var log = new WindowLog(Dispatcher);
        var options = new PluginHostOptions
        {
            Root = root,
            PluginPackageType = "WpfHostPlugin",
            HostOwnedPackages = { "WpfHost.Sdk" },
            LoggerFactory = log,
        };
        if (feed is not null)
        {
            options.Feeds.Add(new PackageFeed(feed) { Name = "sample feed" });
        }

        // Pre-loading and merging plugins' Themes/Plugin.xaml are on by default; the forwarder (process-wide while the
        // host lives) and the view-model → view convention are opt-in.
        options.UseWpf(this, wpf =>
        {
            wpf.ForwardDefaultContextLoads = true;
            wpf.MapViewModelsToViews = true;
        });

        var window = new MainWindow(PluginHost.Create(options), log, feed is not null, install);
        MainWindow = window;
        window.Show();
    }
}

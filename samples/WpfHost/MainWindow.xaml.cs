using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Alcove;
using Alcove.Catalog;
using Alcove.Feeds;
using Alcove.Wpf.Resources;
using Alcove.Wpf.Threading;
using WpfHost.Sdk;

namespace WpfHost;

public partial class MainWindow : Window
{
    private readonly PluginHost _host;
    private readonly DispatcherExtensionCatalog _catalog;
    private readonly bool _hasFeed;
    private readonly IReadOnlyList<string> _startupInstalls;
    private IDisposable? _subscription;
    private bool _disposed;

    internal MainWindow(PluginHost host, WindowLog log, bool hasFeed, IReadOnlyList<string> startupInstalls)
    {
        InitializeComponent();
        _host = host;
        _hasFeed = hasFeed;
        _startupInstalls = startupInstalls;
        Log.ItemsSource = log.Lines;

        // Catalog callbacks on the UI thread, wherever the plugin was loaded (a hot install may continue on the thread pool).
        _catalog = host.Catalog.OnDispatcher(Dispatcher);
        _host.PluginFaulted += (_, e) => Dispatcher.BeginInvoke(() => Status.Text = $"{e.Plugin.Id} faulted: {e.Fault.Message}");
        Loaded += async (_, _) => await StartAsync();
        Closing += OnClosing;
    }

    private async Task StartAsync()
    {
        _subscription = _catalog.Observe<IToolPanel>(AddPanel);
        await _host.LoadAsync();
        if (_hasFeed && _host.Plugins.Count == 0)
        {
            foreach (var id in _startupInstalls)
            {
                await InstallAsync(id);
            }
        }

        await RefreshAsync();
    }

    private void AddPanel(Extension<IToolPanel> extension)
    {
        var title = (extension.Metadata as ToolPanelAttribute)?.Title ?? extension.Plugin.Id;
        try
        {
            Panels.Items.Add(new TabItem { Header = title, Content = extension.GetInstance().CreateContent(), Tag = extension });
            Panels.SelectedIndex = Panels.Items.Count - 1;
        }
        catch (PluginException ex)
        {
            Status.Text = ex.Message;
        }
    }

    private async void OnInstall(object sender, RoutedEventArgs e)
    {
        if (Available.SelectedItem is PluginSearchResult selected)
        {
            await InstallAsync(selected.Id);
            await RefreshAsync();
        }
    }

    private async Task InstallAsync(string id)
    {
        Install.IsEnabled = false;
        Status.Text = $"Installing {id}...";
        try
        {
            var result = await _host.InstallAsync(id);
            Status.Text = $"{id}: {result.Status}{(result.Reason is null ? "" : $" ({result.Reason})")}";
        }
        finally
        {
            Install.IsEnabled = true;
        }
    }

    private async Task RefreshAsync()
    {
        Plugins.ItemsSource = _host.Plugins.Select(p => $"{p.Id} {p.Version} · cell '{p.Cell}' · {p.Status}").ToList();
        if (_hasFeed)
        {
            var installed = _host.Plugins.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Available.ItemsSource = (await _host.SearchAsync("")).Where(r => r.IsCompatible && !installed.Contains(r.Id)).ToList();
        }
    }

    private void OnPopOut(object sender, RoutedEventArgs e)
    {
        // Panels are transient: a new instance for the window. A plugin contributes windows this way.
        if (Panels.SelectedItem is TabItem { Tag: Extension<IToolPanel> extension } tab)
        {
            new Window
            {
                Title = $"{tab.Header} ({extension.Plugin.Id})",
                Content = extension.GetInstance().CreateContent(),
                Width = 500,
                Height = 400,
                Owner = this,
            }.Show();
        }
    }

    private void OnSwitchAccent(object sender, RoutedEventArgs e)
    {
        // Every plugin uses the key through DynamicResource, so they all follow.
        var current = Application.Current.TryFindResource(SampleKeys.AccentBrush, out var brush) ? (brush as SolidColorBrush)?.Color : null;
        Application.Current.Resources.SetResource<Brush>(SampleKeys.AccentBrush, new SolidColorBrush(current == Colors.DarkOrange ? Color.FromRgb(0x2B, 0x6C, 0xB0) : Colors.DarkOrange));
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        // Dispose the host (plugins, then the merged dictionaries and the forwarder) before the app ends; await it on the
        // UI thread rather than blocking on it. Close again from the dispatcher: if the disposal completed synchronously,
        // we're still in this Closing event, where Close() throws.
        e.Cancel = true;
        _subscription?.Dispose();
        await _host.DisposeAsync();
        _disposed = true;
        _ = Dispatcher.InvokeAsync(Close);
    }
}

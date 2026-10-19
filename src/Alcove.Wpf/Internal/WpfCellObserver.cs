using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;

namespace Alcove.Wpf.Internal;

/// <summary>
/// What <c>UseWpf</c> adds to each host: pre-loading, the opt-in default-context forwarder, and merging plugins'
/// resource dictionaries and view templates (removed again when the host is disposed).
/// </summary>
internal sealed class WpfCellObserver : ICellObserver, IDisposable
{
    /// <summary>The switch the host should set (spike: wpf-across-load-contexts, §4 Q7.10).</summary>
    public const string AppendVersionSwitch = "Switch.System.Windows.Baml2006.AppendLocalAssemblyVersionForSourceUri";

    private readonly WpfPluginOptions _options;
    private readonly ILogger _logger;
    private readonly DefaultContextForwarder? _forwarder;

    // Only touched on the dispatcher's thread.
    private readonly List<Merged> _merged = [];
    private bool _disposed;

    public WpfCellObserver(WpfPluginOptions options, ILoggerFactory loggerFactory)
    {
        _options = options;
        _logger = loggerFactory.CreateLogger("Alcove.Wpf");
        if (options.ForwardDefaultContextLoads)
        {
            _forwarder = new DefaultContextForwarder(_logger);
            _forwarder.Register();
        }

        if (!AppContext.TryGetSwitch(AppendVersionSwitch, out var enabled) || !enabled)
        {
            WpfLog.SwitchNotSet(_logger, AppendVersionSwitch);
        }
    }

    /// <summary>The forwarder, if <see cref="WpfPluginOptions.ForwardDefaultContextLoads"/> is on (tests).</summary>
    internal DefaultContextForwarder? Forwarder => _forwarder;

    public void CellCreated(CellInfo cell)
    {
        _forwarder?.Add(cell);
        if (_options.PreloadWpfAssemblies)
        {
            WpfPreloader.Preload(cell, cell.PrivateAssemblies, _logger);
        }
    }

    public void CellExtended(CellInfo cell, IReadOnlyList<CellAssembly> added)
    {
        if (_options.PreloadWpfAssemblies)
        {
            WpfPreloader.Preload(cell, added, _logger);
        }
    }

    public void PluginLoading(CellInfo cell, PluginInfo plugin, IReadOnlyList<Assembly> assemblies)
    {
        var parts = _options.MergePluginResources
            ? assemblies.SelectMany(a => PluginResourceLocator.Find(a).Select(path => (Assembly: a, Path: path))).ToList()
            : [];
        var views = _options.MapViewModelsToViews ? assemblies.SelectMany(ViewTemplates.FindPairs).ToList() : [];
        if (parts.Count == 0 && views.Count == 0)
        {
            return;
        }

        // Synchronously, so the plugin's resources are in place before any of its extensions can be created.
        Run(() =>
        {
            if (_disposed)
            {
                return;   // a plugin loaded by an operation the host's disposal didn't wait for: nothing would remove them
            }

            using var scope = cell.LoadContext.EnterContextualReflection();
            var target = _options.Resources ?? Application.Current?.Resources;
            if (target is null)
            {
                WpfLog.NoResourceTarget(_logger, plugin.Id);
                return;
            }

            var container = new ResourceDictionary();
            foreach (var (assembly, path) in parts)
            {
                container.MergedDictionaries.Add(new ResourceDictionary { Source = PluginPackUri.Create(assembly, path) });
            }

            foreach (var (viewModel, view) in views)
            {
                var template = ViewTemplates.Create(viewModel, view);
                container.Add(template.DataTemplateKey!, template);
            }

            // Below the app's own dictionaries (and after earlier plugins'), so the app's resources win.
            var index = 0;
            for (var i = _merged.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(_merged[i].Target, target) && target.MergedDictionaries.IndexOf(_merged[i].Container) is >= 0 and var at)
                {
                    index = at + 1;
                    break;
                }
            }

            target.MergedDictionaries.Insert(index, container);
            _merged.Add(new Merged(plugin.Id, container, target));
            if (parts.Count > 0)
            {
                WpfLog.Merged(_logger, parts.Count, plugin.Id, string.Join(", ", parts.Select(p => PluginPackUri.Format(p.Assembly, p.Path))));
            }

            if (views.Count > 0)
            {
                WpfLog.ViewsMapped(_logger, views.Count, plugin.Id, string.Join(", ", views.Select(v => $"{v.ViewModel.Name} → {v.View.Name}")));
            }
        });
    }

    public void Dispose()
    {
        _forwarder?.Dispose();
        if (_options.Dispatcher.HasShutdownStarted)
        {
            return;   // nothing left to show them
        }

        Run(() =>
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            for (var i = _merged.Count - 1; i >= 0; i--)
            {
                var merged = _merged[i];
                try
                {
                    merged.Target.MergedDictionaries.Remove(merged.Container);
                }
                catch (Exception ex)
                {
                    WpfLog.UnmergeFailed(_logger, ex, merged.PluginId);
                }
            }

            _merged.Clear();
        });
    }

    /// <summary>Runs on the dispatcher's thread: inline when already there, otherwise with a blocking <see cref="Dispatcher.Invoke(Action)"/>.</summary>
    private void Run(Action action)
    {
        if (_options.Dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _options.Dispatcher.Invoke(action);
        }
    }

    private sealed record Merged(string PluginId, ResourceDictionary Container, ResourceDictionary Target);
}

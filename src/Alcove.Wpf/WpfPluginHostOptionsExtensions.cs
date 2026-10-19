using System.Windows;
using System.Windows.Threading;
using Alcove.Wpf.Internal;

namespace Alcove.Wpf;

/// <summary>Sets up a plugin host for a WPF app.</summary>
/// <remarks>
/// <para>
/// WPF knows nothing about load contexts: it finds assemblies by simple name among everything loaded in the process,
/// and its own fallback loads go to the default context, never to a cell. <c>UseWpf</c> makes plugins' XAML work
/// inside cells: it pre-loads their WPF-relevant assemblies, merges their resource dictionaries with versioned pack
/// URIs, and optionally forwards WPF's failed default-context loads to the owning cell. Extensions are already created
/// inside their cell's contextual reflection scope by the core, whichever activator is set (including those of
/// <c>Alcove.DependencyInjection</c> and <c>Alcove.Autofac</c>), so <c>UseWpf</c> combines with them in any order.
/// </para>
/// <para>
/// Also set this switch in the host's project file, so XAML that refers to its own assembly's dictionaries gets a
/// versioned URI (the host logs a message when it isn't set):
/// <code>&lt;RuntimeHostConfigurationOption Include="Switch.System.Windows.Baml2006.AppendLocalAssemblyVersionForSourceUri" Value="true" /&gt;</code>
/// </para>
/// <para>
/// Use <see cref="DispatcherExtensionCatalogExtensions.OnDispatcher"/> to get catalog callbacks on the UI thread.
/// </para>
/// </remarks>
public static class WpfPluginHostOptionsExtensions
{
    /// <summary>The package ID that <c>UseWpf</c> makes host-owned.</summary>
    internal const string PackageId = "Alcove.Wpf";

    /// <summary>Sets up the options for a WPF app: plugins' resources are merged into <paramref name="application"/>'s resources.</summary>
    /// <param name="options">The options.</param>
    /// <param name="application">The app (its <see cref="DispatcherObject.Dispatcher"/> and <see cref="Application.Resources"/>).</param>
    /// <param name="configure">Changes the defaults, e.g. turns on <see cref="WpfPluginOptions.ForwardDefaultContextLoads"/>.</param>
    /// <returns><paramref name="options"/>.</returns>
    /// <exception cref="InvalidOperationException"><c>UseWpf</c> was already called on these options.</exception>
    public static PluginHostOptions UseWpf(this PluginHostOptions options, Application application, Action<WpfPluginOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(application);
        return UseWpf(options, new WpfPluginOptions(application.Dispatcher, application.Resources), configure);
    }

    /// <summary>
    /// Sets up the options for a WPF UI thread without a given <see cref="Application"/>: plugins' resources are merged into
    /// <see cref="WpfPluginOptions.Resources"/>, or <see cref="Application.Current"/>'s resources when it isn't set.
    /// </summary>
    /// <param name="options">The options.</param>
    /// <param name="dispatcher">The UI thread's dispatcher.</param>
    /// <param name="configure">Changes the defaults.</param>
    /// <returns><paramref name="options"/>.</returns>
    /// <exception cref="InvalidOperationException"><c>UseWpf</c> was already called on these options.</exception>
    public static PluginHostOptions UseWpf(this PluginHostOptions options, Dispatcher dispatcher, Action<WpfPluginOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        return UseWpf(options, new WpfPluginOptions(dispatcher, resources: null), configure);
    }

    private static PluginHostOptions UseWpf(PluginHostOptions options, WpfPluginOptions wpf, Action<WpfPluginOptions>? configure)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.CellObservers.Any(f => f.Target is WpfSetup))
        {
            throw new InvalidOperationException("UseWpf was already called on these options.");
        }

        configure?.Invoke(wpf);
        options.CellObservers.Add(new WpfSetup(wpf).CreateObserver);
        if (!options.HostOwnedPackages.Contains(PackageId, StringComparer.OrdinalIgnoreCase))
        {
            options.HostOwnedPackages.Add(PackageId);
        }

        return options;
    }

    /// <summary>Creates each host's observer from the configured options.</summary>
    private sealed class WpfSetup(WpfPluginOptions options)
    {
        public ICellObserver CreateObserver(Microsoft.Extensions.Logging.ILoggerFactory loggerFactory) => new WpfCellObserver(options, loggerFactory);
    }
}

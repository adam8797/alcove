using System.Windows;
using System.Windows.Threading;

namespace Alcove.Wpf;

/// <summary>What <see cref="WpfPluginHostOptionsExtensions.UseWpf(PluginHostOptions, Application, Action{WpfPluginOptions}?)"/> sets up.</summary>
public sealed class WpfPluginOptions
{
    internal WpfPluginOptions(Dispatcher dispatcher, ResourceDictionary? resources)
    {
        Dispatcher = dispatcher;
        Resources = resources;
    }

    /// <summary>The UI thread's dispatcher: resource dictionaries are merged and removed on it.</summary>
    public Dispatcher Dispatcher { get; }

    /// <summary>
    /// Where plugins' resource dictionaries are merged. Defaults to the application's resources (the one passed to
    /// <c>UseWpf</c>, or <see cref="Application.Current"/> when a dispatcher was passed).
    /// </summary>
    public ResourceDictionary? Resources { get; set; }

    /// <summary>
    /// Loads every WPF-relevant private assembly of a cell when the cell is created (and those a hot install adds to
    /// it), so XAML that uses a control library only from markup finds it. WPF never asks a cell for an assembly; it
    /// only finds what is already loaded. On by default.
    /// </summary>
    public bool PreloadWpfAssemblies { get; set; } = true;

    /// <summary>
    /// Opt-in: registers a handler on the default load context's <c>Resolving</c> event for the host's lifetime, which
    /// forwards a failed default-context load to the single cell owning a private copy with that simple name (any
    /// version). This catches WPF's own fallback loads, which bind in the default context: a XAML library rolled forward
    /// to a newer version in its cell, and external theme assemblies. When several cells own different copies it
    /// doesn't choose and logs a warning instead.
    /// </summary>
    /// <remarks>
    /// It's process-wide behaviour (the default context belongs to the process), registered when the host is created
    /// and removed when it's disposed: the one deliberate exception to Alcove's "no global state" (ADR 0012).
    /// </remarks>
    public bool ForwardDefaultContextLoads { get; set; }

    /// <summary>
    /// Merges each plugin assembly's resource dictionaries (<see cref="PluginResourcesAttribute"/>, or by convention
    /// <c>Themes/Plugin.xaml</c>) into <see cref="Resources"/> when the plugin loads, before it registers its extensions,
    /// with versioned pack URIs (<see cref="PluginPackUri"/>). They're merged below the application's own dictionaries,
    /// so the app's resources win, and removed when the host is disposed. On by default.
    /// </summary>
    public bool MergePluginResources { get; set; } = true;

    /// <summary>
    /// Opt-in: when a plugin loads, gives each of its view-models a view by naming convention (<c>FooViewModel</c> →
    /// <c>FooView</c>, see <see cref="ViewTemplates.CreateForAssembly"/>) as an implicit <see cref="DataTemplate"/> in
    /// <see cref="Resources"/>, so a <c>ContentControl</c> showing the view-model shows its view.
    /// </summary>
    public bool MapViewModelsToViews { get; set; }
}

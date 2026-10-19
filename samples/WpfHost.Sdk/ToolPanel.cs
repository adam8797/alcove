using System.Windows;
using Alcove;

namespace WpfHost.Sdk;

/// <summary>The extension point: a panel the host shows in a tab (or pops out into a window).</summary>
public interface IToolPanel
{
    /// <summary>Creates the panel's content. Called on the UI thread, once per tab or window.</summary>
    FrameworkElement CreateContent();
}

/// <summary>Marks a tool panel and gives it a title, readable without creating it.</summary>
public sealed class ToolPanelAttribute : ExtensionAttribute
{
    public ToolPanelAttribute(string title)
        : base(typeof(IToolPanel))
    {
        Title = title;
        Lifetime = ExtensionLifetime.Transient;   // a new panel per tab or window
    }

    public string Title { get; }
}

using System.Windows;
using System.Windows.Controls;
using Sample.Notes.ViewModels;
using WpfHost.Sdk;

namespace Sample.Notes;

[ToolPanel("Notes")]
public sealed class NotesPanel : IToolPanel
{
    // The host's implicit DataTemplate (UseWpf: MapViewModelsToViews) shows NotesView for the view-model.
    public FrameworkElement CreateContent() => new ContentControl { Content = new NotesViewModel() };
}

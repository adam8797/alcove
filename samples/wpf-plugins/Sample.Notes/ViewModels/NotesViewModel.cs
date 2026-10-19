namespace Sample.Notes.ViewModels;

public sealed class NotesViewModel
{
    public string Title => "Notes";

    public string Text { get; set; } = "Write something. This view was found by convention: NotesViewModel → NotesView.";
}

using ConsoleHost.Contracts;
using Sample.Text;

namespace Sample.Formatting;

/// <summary>Formatting helpers for other plugins.</summary>
public static class Table
{
    public static TextBlock Render(IEnumerable<(string Key, string Value)> rows)
    {
        var list = rows.ToList();
        var width = list.Count == 0 ? 0 : list.Max(r => r.Key.Length);
        var block = new TextBlock();
        foreach (var (key, value) in list)
        {
            block.Add($"{key.PadRight(width)} : {value}");
        }

        return block;
    }
}

[Command(Name = "formatting", Description = "Where the formatting plugin runs")]
public sealed class FormattingCommand : ICommand
{
    public string Run() => "Formatting with " + TextInfo.Describe();
}

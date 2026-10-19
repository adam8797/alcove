using System.Runtime.Loader;

namespace Sample.Text;

/// <summary>Which copy of this library is running: version and cell.</summary>
public static class TextInfo
{
#if V2
    public static string Version => "2.0";
#else
    public static string Version => "1.0";
#endif

    public static string Cell => AssemblyLoadContext.GetLoadContext(typeof(TextInfo).Assembly)?.Name ?? "?";

    public static string Describe() => $"Sample.Text {Version} in cell '{Cell}'";
}

/// <summary>Lines of text. Plugins that put it in their public API make Sample.Text an exposed dependency.</summary>
public sealed class TextBlock
{
    private readonly List<string> _lines = [];

    public IReadOnlyList<string> Lines => _lines;

    public TextBlock Add(string line)
    {
        _lines.Add(line);
        return this;
    }

#if V2
    /// <summary>New in 2.0.</summary>
    public string Framed()
    {
        var width = _lines.Count == 0 ? 0 : _lines.Max(l => l.Length);
        var border = "+" + new string('-', width + 2) + "+";
        return string.Join('\n', [border, .. _lines.Select(l => $"| {l.PadRight(width)} |"), border]);
    }
#endif

    public override string ToString() => string.Join('\n', _lines);
}

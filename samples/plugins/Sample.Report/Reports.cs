using System.Runtime.Loader;
using ConsoleHost.Contracts;
using Sample.Formatting;
using Sample.Text;

namespace Sample.Report;

/// <summary>Builds reports with Sample.Formatting; used by Sample.Dashboard.</summary>
public static class Reports
{
    // Sample.Text is an exposed dependency of Sample.Formatting, so this cell gets it from Formatting's cell:
    // TextInfo reports that cell, and TextBlocks pass between the two plugins.
    public static TextBlock Summary() => Table.Render(
    [
        ("report runs in", $"cell '{AssemblyLoadContext.GetLoadContext(typeof(Reports).Assembly)?.Name}'"),
        ("TextBlock from", TextInfo.Describe()),
    ]);
}

[Command(Name = "report", Description = "A report formatted by another plugin")]
public sealed class ReportCommand : ICommand
{
    public string Run() => Reports.Summary().Framed();
}

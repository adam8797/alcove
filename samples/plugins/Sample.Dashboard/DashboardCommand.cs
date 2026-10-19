using ConsoleHost.Contracts;
using Sample.Text;

namespace Sample.Dashboard;

[Command(Name = "dashboard", Description = "Shows the report (a chain of three plugins)")]
public sealed class DashboardCommand : ICommand
{
    public string Run()
    {
        TextBlock summary = Report.Reports.Summary();
        return summary.Add("shown by the dashboard").Framed();
    }
}

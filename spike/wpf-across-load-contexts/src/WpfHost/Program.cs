using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HostSdk;

namespace WpfHost;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--list"))
        {
            foreach (var s in Scenarios.All) Console.WriteLine($"{s.Id,-4} {s.Question,-4} {s.Title}");
            return 0;
        }

        int i = Array.IndexOf(args, "--scenario");
        if (i >= 0 && i + 1 < args.Length) return RunInProcess(args[i + 1]);

        if (args.Contains("--all")) return RunAll();

        return Interactive();
    }

    /// <summary>Runs one scenario in this process inside a WPF Application. Exit code 0 = all checks matched predictions.</summary>
    private static int RunInProcess(string id)
    {
        var scenario = Scenarios.All.FirstOrDefault(s => s.Id == id);
        if (scenario is null)
        {
            Console.WriteLine($"unknown scenario '{id}' (use --list)");
            return 3;
        }

        Scenarios.ApplySwitches(id);
        int exit = 0;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += Harness.OnDispatcherUnhandled;
        app.Startup += (_, _) =>
        {
            var h = new Harness(id);
            Console.WriteLine($"=== SCENARIO {id} [{scenario.Question}] {scenario.Title}");
            try
            {
                // What any host would do: the host-owned SDK's styles/keys are app-level resources.
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = HostKeys.ThemeUri });
                scenario.Run(h);
                int pass = h.Results.Count(r => r.Actual == Outcome.Pass);
                int matched = h.Results.Count(r => r.Actual == r.Predicted);
                Console.WriteLine($"=== RESULT {id}: {pass} PASS, {h.Results.Count - pass} FAIL; predictions matched {matched}/{h.Results.Count}" +
                                  (matched == h.Results.Count ? "" : "  <-- SOME PREDICTIONS WRONG"));
                exit = matched == h.Results.Count ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"=== RESULT {id}: CRASHED  {Harness.Describe(ex)}");
                Console.WriteLine(ex);
                exit = 2;
            }
            finally
            {
                Console.Out.Flush();
                app.Shutdown(exit);
            }
        };
        app.Run();
        return exit;
    }

    /// <summary>Runs every scenario in a fresh child process and writes results/summary.txt.</summary>
    private static int RunAll()
    {
        var all = new StringBuilder();
        var summary = new List<string>();
        int worst = 0;
        foreach (var s in Scenarios.All)
        {
            var (code, output) = RunChild(s.Id);
            Console.Write(output);
            all.Append(output);
            string resultLine = output.Split('\n').LastOrDefault(l => l.StartsWith("=== RESULT", StringComparison.Ordinal))?.Trim()
                                ?? $"=== RESULT {s.Id}: no result line (exit code {code})";
            summary.Add($"{s.Id,-4} {s.Question,-4} exit={code}  {resultLine["=== RESULT ".Length..]}");
            worst = Math.Max(worst, code);
        }

        var text = new StringBuilder();
        text.AppendLine();
        text.AppendLine("==================== SUMMARY ====================");
        foreach (var l in summary) text.AppendLine(l);
        text.AppendLine(worst == 0 ? "ALL PREDICTIONS MATCHED" : "SOME PREDICTIONS DID NOT MATCH (or a scenario crashed) - see !! UNEXPECTED lines above");
        Console.Write(text);
        all.Append(text);

        Directory.CreateDirectory("results");
        File.WriteAllText(Path.Combine("results", "summary.txt"), all.ToString());
        Console.WriteLine($"Full output written to {Path.GetFullPath(Path.Combine("results", "summary.txt"))}");
        return worst;
    }

    private static (int Code, string Output) RunChild(string id)
    {
        // Works both for WpfHost.exe and for "dotnet WpfHost.dll".
        string exe = Environment.ProcessPath!;
        string arguments = Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? $"\"{typeof(Program).Assembly.Location}\" --scenario {id}"
            : $"--scenario {id}";
        var psi = new ProcessStartInfo(exe, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Environment.CurrentDirectory,
        };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(TimeSpan.FromSeconds(90)))
        {
            p.Kill(entireProcessTree: true);
            return (9, $"=== SCENARIO {id}\n=== RESULT {id}: TIMEOUT after 90 s\n");
        }
        string output = stdout.Result + (string.IsNullOrWhiteSpace(stderr.Result) ? "" : "--- stderr ---\n" + stderr.Result);
        return (p.ExitCode, output.ReplaceLineEndings("\n") + "\n");
    }

    /// <summary>No arguments: a small window with one button per scenario (each runs in a child process).</summary>
    private static int Interactive()
    {
        var app = new Application();
        var output = new TextBox
        {
            FontFamily = new FontFamily("Consolas"),
            IsReadOnly = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var buttons = new WrapPanel();
        foreach (var s in Scenarios.All.Where(s => s.Id != "0"))
        {
            var b = new Button { Content = $"{s.Id} {s.Question}", ToolTip = s.Title, Margin = new Thickness(2), Padding = new Thickness(6, 2, 6, 2) };
            b.Click += async (_, _) =>
            {
                output.Text = $"running {s.Id}...";
                var (_, text) = await Task.Run(() => RunChild(s.Id));
                output.Text = text;
            };
            buttons.Children.Add(b);
        }
        var dock = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Top);
        dock.Children.Add(buttons);
        dock.Children.Add(output);
        return app.Run(new Window { Title = "WPF across load contexts (spike)", Width = 1100, Height = 750, Content = dock });
    }
}

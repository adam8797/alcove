using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace WpfHost;

/// <summary>Outcome of a check: did WPF behave correctly from the plugin's point of view?</summary>
public enum Outcome { Pass, Fail }

/// <summary>Records checks, compares them with the prediction from the WPF source reading, prints evidence.</summary>
public sealed class Harness
{
    private readonly List<(string Id, Outcome Actual, Outcome Predicted)> _results = new();
    private static readonly List<Exception> UnhandledDuringCheck = new();

    public string ScenarioId { get; }

    public Harness(string scenarioId) => ScenarioId = scenarioId;

    public IReadOnlyList<(string Id, Outcome Actual, Outcome Predicted)> Results => _results;

    public static void OnDispatcherUnhandled(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        UnhandledDuringCheck.Add(e.Exception);
        e.Handled = true;
    }

    /// <summary>
    /// Runs one check. <paramref name="body"/> returns (ok, detail); an exception means Fail.
    /// </summary>
    public void Check(string id, string description, Outcome predicted, string predictionWhy, Func<(bool Ok, string Detail)> body)
    {
        UnhandledDuringCheck.Clear();
        Outcome actual;
        string detail;
        try
        {
            var (ok, d) = body();
            Pump();
            if (UnhandledDuringCheck.Count > 0)
            {
                ok = false;
                d += " | dispatcher exception: " + Describe(UnhandledDuringCheck[0]);
            }
            actual = ok ? Outcome.Pass : Outcome.Fail;
            detail = d;
        }
        catch (Exception ex)
        {
            actual = Outcome.Fail;
            detail = "EXCEPTION " + Describe(ex);
        }

        _results.Add((id, actual, predicted));
        string match = actual == predicted ? "matches prediction" : "!! UNEXPECTED (prediction was wrong)";
        Console.WriteLine($"  CHECK {id,-5} {(actual == Outcome.Pass ? "PASS" : "FAIL"),-4}  predicted {(predicted == Outcome.Pass ? "PASS" : "FAIL"),-4}  {match}");
        Console.WriteLine($"        what:      {description}");
        Console.WriteLine($"        observed:  {detail}");
        Console.WriteLine($"        predicted: {predictionWhy}");
    }

    public static string Describe(Exception ex)
    {
        var parts = new List<string>();
        for (var e = ex; e != null; e = e.InnerException)
            parts.Add($"{e.GetType().Name}: {e.Message.ReplaceLineEndings(" ")}");
        return string.Join("  --->  ", parts);
    }

    // ---------- WPF helpers ----------

    public static void Pump()
    {
        // Run the dispatcher queue down to idle (layout, template application, bindings, Loaded events).
        Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
    }

    /// <summary>Shows content in an off-screen window, lays it out, saves a PNG, returns the window (caller closes).</summary>
    public Window Show(FrameworkElement content, string name)
    {
        var w = new Window
        {
            Title = $"{ScenarioId} {name}",
            Content = content,
            Width = 420,
            Height = 320,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
            Top = -20000,
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStyle = WindowStyle.None,
            Background = Brushes.White,
        };
        w.Show();
        w.UpdateLayout();
        Pump();
        SavePng(w, name);
        return w;
    }

    private void SavePng(Window w, string name)
    {
        try
        {
            if (w.Content is not FrameworkElement fe || fe.ActualWidth < 1 || fe.ActualHeight < 1) return;
            var bmp = new RenderTargetBitmap((int)fe.ActualWidth, (int)fe.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(fe);
            var dir = Path.Combine(Environment.CurrentDirectory, "results");
            Directory.CreateDirectory(dir);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = File.Create(Path.Combine(dir, $"scenario{ScenarioId}-{name}.png"));
            enc.Save(fs);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"      (png not saved: {Describe(ex)})");
        }
    }

    /// <summary>Every TextBlock.Text in the visual tree (includes template-generated content).</summary>
    public static List<string> Texts(DependencyObject root)
    {
        var list = new List<string>();
        void Walk(DependencyObject d)
        {
            if (d is TextBlock tb && !string.IsNullOrEmpty(tb.Text)) list.Add(tb.Text);
            int n = VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < n; i++) Walk(VisualTreeHelper.GetChild(d, i));
        }
        Walk(root);
        return list;
    }

    public static string Marks(DependencyObject root) =>
        string.Join(", ", Texts(root).Where(t => t.StartsWith("MARK:", StringComparison.Ordinal)).Select(t => t[5..]));

    public static string AlcOf(object o) => AlcOf(o.GetType().Assembly);

    public static string AlcOf(Assembly a) => AssemblyLoadContext.GetLoadContext(a)?.Name ?? "?";

    public static string Who(object o) => $"{o.GetType().FullName} [{o.GetType().Assembly.GetName().Version}, alc={AlcOf(o)}]";
}

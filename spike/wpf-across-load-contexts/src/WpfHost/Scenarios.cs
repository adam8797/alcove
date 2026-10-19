using System.IO;
using System.Runtime.Loader;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using HostSdk;
using static WpfHost.Harness;
using static WpfHost.Outcome;

namespace WpfHost;

public sealed record Scenario(string Id, string Question, string Title, Action<Harness> Run);

/// <summary>
/// Every scenario runs in a fresh process (WPF and the runtime keep process-wide state: AppDomain assembly
/// list, WPF's ResourceManagerWrapper cache, Default-context bind cache). Predictions come from reading
/// dotnet/wpf release/10.0 (see REPORT.md, Q7).
/// </summary>
public static class Scenarios
{
    private static string PluginsDir => Path.Combine(AppContext.BaseDirectory, "plugins");

    private const string NotFoundFmt = "does not have a resource identified by the URI";

    public static readonly IReadOnlyList<Scenario> All = new List<Scenario>
    {
        new("0",   "-",  "Environment info", Env),
        new("1",   "Q1", "UserControl with compiled BAML from a plugin in a compartment", S1),
        new("2",   "Q2", "pack:// ResourceDictionary + Application.LoadComponent for a plugin assembly", S2),
        new("3",   "Q3", "XAML referencing own / host-owned / other plugin's types", S3),
        new("4",   "Q4", "Implicit DataTemplate (DataType=x:Type) from a plugin dictionary", S4),
        new("5",   "Q5", "Duplicate name, DIFFERENT AssemblyVersion: SharedControls 1.0.0.0 (A) + 2.0.0.0 (B)", S5),
        new("6",   "Q6", "DynamicResource from plugin XAML to host keys (HostSdk)", S6),
        new("7",   "Q5+", "Duplicate name, SAME AssemblyVersion, different content: SharedControls v1 (A) + v1b (C)", S7),
        new("8",   "Q5+", "Same plugin assembly (identical bytes) in two compartments", S8),
        new("9a",  "Q3+", "XAML-only reference to a compartment-private assembly that is not loaded yet (no mitigation)", s => S9(s, Mitigation.None)),
        new("9b",  "Q3+", "... same, creating the view inside EnterContextualReflection(compartment)", s => S9(s, Mitigation.ContextualReflection)),
        new("9c",  "Q3+", "... same, after eagerly loading all compartment assemblies", s => S9(s, Mitigation.EagerLoad)),
        new("9d",  "Q3+", "... same, with a Default.Resolving forwarder to the owning compartment", s => S9(s, Mitigation.DefaultForwarder)),
        new("10a", "Q5+", "Private dependency rolled forward (compiled vs 1.0.0.0, deployed 2.0.0.0), eager load, no mitigation", s => S10(s, Mitigation.None)),
        new("10b", "Q5+", "... same, creating the view inside EnterContextualReflection(compartment)", s => S10(s, Mitigation.ContextualReflection)),
        new("10c", "Q5+", "... same, with a Default.Resolving forwarder to the owning compartment", s => S10(s, Mitigation.DefaultForwarder)),
        new("11a", "Q5+", "Library dictionary merging its OWN dictionary unversioned, v1 + v2 loaded, AppContext switch OFF (default)", s => S11(s, switchOn: false)),
        new("11b", "Q5+", "... same with Switch.System.Windows.Baml2006.AppendLocalAssemblyVersionForSourceUri = true", s => S11(s, switchOn: true)),
    };

    /// <summary>Scenarios that need an AppContext switch set before WPF reads it (it is cached on first read).</summary>
    public static void ApplySwitches(string id)
    {
        if (id == "11b") AppContext.SetSwitch("Switch.System.Windows.Baml2006.AppendLocalAssemblyVersionForSourceUri", true);
    }

    private enum Mitigation { None, ContextualReflection, EagerLoad, DefaultForwarder }

    // ------------------------------------------------------------------ helpers

    private static Compartment NewCompartment(string name, string folder, bool eager = true, params Compartment[] imports)
    {
        Console.WriteLine($"    > create compartment '{name}' over plugins/{folder} (eager={eager})");
        var c = new Compartment(name, Path.Combine(PluginsDir, folder));
        foreach (var other in imports)
        {
            foreach (var asm in other.Assemblies.Where(a => a.GetName().Name!.StartsWith("Plugin", StringComparison.Ordinal)))
                c.Import(asm);
        }
        if (eager) c.EagerLoadAll();
        return c;
    }

    private static FrameworkElement CreateShared(Compartment c, string typeName)
    {
        var asm = c.LoadByName("SharedControls");
        var type = asm.GetType("SharedControls." + typeName, throwOnError: true)!;
        try
        {
            return (FrameworkElement)Activator.CreateInstance(type)!;
        }
        catch (System.Reflection.TargetInvocationException tie) when (tie.InnerException != null)
        {
            throw tie.InnerException;
        }
    }

    private static (bool, string) ViewShows(Harness h, Func<FrameworkElement> create, string name, string mustContain, string? namedElement = null, Func<object, bool>? namedCheck = null)
    {
        var view = create();
        var w = h.Show(view, name);
        try
        {
            string marks = Marks(view);
            bool ok = marks.Contains(mustContain, StringComparison.Ordinal);
            string detail = $"shows [{marks}]";
            if (namedElement != null)
            {
                var el = view.FindName(namedElement);
                detail += $"; {namedElement} = {(el == null ? "null" : Who(el))}";
                if (namedCheck != null) ok &= el != null && namedCheck(el);
            }
            return (ok, detail);
        }
        finally
        {
            w.Close();
        }
    }

    private static string? SharedText(ResourceDictionary rd) => rd.Contains("Shared.VersionText") ? rd["Shared.VersionText"] as string : null;

    private static ResourceDictionary LoadRd(string uri) =>
        new() { Source = new Uri(uri, uri.StartsWith("pack:", StringComparison.Ordinal) ? UriKind.Absolute : UriKind.Relative) };

    // ------------------------------------------------------------------ 0

    private static void Env(Harness h)
    {
        Console.WriteLine($"    OS:          {Environment.OSVersion}");
        Console.WriteLine($"    Runtime:     {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        var pf = typeof(Window).Assembly;
        Console.WriteLine($"    WPF:         {pf.FullName}");
        Console.WriteLine($"    WPF file:    {System.Diagnostics.FileVersionInfo.GetVersionInfo(pf.Location).FileVersion}  ({pf.Location})");
        Console.WriteLine($"    Plugins dir: {PluginsDir}");
        foreach (var d in Directory.GetDirectories(PluginsDir).Order())
            Console.WriteLine($"      {Path.GetFileName(d)}: {string.Join(", ", Directory.GetFiles(d, "*.dll").Select(f => $"{Path.GetFileName(f)} {System.Reflection.AssemblyName.GetAssemblyName(f).Version}"))}");
        h.Check("0.1", "SharedControls is NOT in the host's default context / output folder", Pass,
            "the build target errors out if it leaked",
            () => (!File.Exists(Path.Combine(AppContext.BaseDirectory, "SharedControls.dll")), "host folder clean"));
    }

    // ------------------------------------------------------------------ 1  (Q1)

    private static void S1(Harness h)
    {
        var a = NewCompartment("A", "PluginA");
        var entry = a.CreateEntry("PluginA");

        h.Check("1.1", "Plugin A's UserControl constructs (InitializeComponent -> Application.LoadComponent -> BAML) and renders", Pass,
            "HIGH. Only one PluginA is loaded; SafeSecurityHelper.GetLoadedAssembly finds it in AppDomain.GetAssemblies() regardless of ALC; Application.LoadComponent's stream-assembly check matches.",
            () => ViewShows(h, entry.CreateView, "A-view", "PluginA.PluginAView"));

        h.Check("1.2", "The view's type lives in compartment A, not the default context", Pass,
            "HIGH. Type comes from the assembly we loaded into A.",
            () =>
            {
                var v = entry.CreateView();
                return (AlcOf(v) == "A", Who(v));
            });
    }

    // ------------------------------------------------------------------ 2  (Q2)

    private static void S2(Harness h)
    {
        var a = NewCompartment("A", "PluginA");
        a.CreateEntry("PluginA");

        h.Check("2.1", "new ResourceDictionary { Source = pack://application:,,,/PluginA;component/Themes/Plugin.xaml }", Pass,
            "HIGH. ResourceContainer.GetResourceManagerWrapper -> BaseUriHelper.GetLoadedAssembly finds the only PluginA.",
            () =>
            {
                var rd = LoadRd("pack://application:,,,/PluginA;component/Themes/Plugin.xaml");
                return ((rd["PluginA.Greeting"] as string) == "MARK:PluginA.ThemeDictionary", $"PluginA.Greeting = {rd["PluginA.Greeting"]}");
            });

        h.Check("2.2", "Merge it into Application.Resources; TryFindResource from the app finds the plugin key", Pass,
            "HIGH. Plain dictionary merge.",
            () =>
            {
                Application.Current.Resources.MergedDictionaries.Add(LoadRd("pack://application:,,,/PluginA;component/Themes/Plugin.xaml"));
                var v = Application.Current.TryFindResource("PluginA.Greeting");
                return ((v as string) == "MARK:PluginA.ThemeDictionary", $"TryFindResource = {v}");
            });

        h.Check("2.3", "Application.LoadComponent(new Uri(\"/PluginA;component/Themes/Plugin.xaml\", Relative))", Pass,
            "HIGH. Application.LoadComponent(Uri) -> same ResourceContainer path; returns the ResourceDictionary.",
            () =>
            {
                var o = Application.LoadComponent(new Uri("/PluginA;component/Themes/Plugin.xaml", UriKind.Relative));
                return (o is ResourceDictionary rd && rd.Contains("PluginA.Greeting"), $"returned {o?.GetType().FullName}");
            });

        h.Check("2.4", "Versioned form /PluginA;v1.0.0.0;component/Themes/Plugin.xaml", Pass,
            "HIGH. BaseUriHelper.GetAssemblyNameAndPart parses ';v1.0.0.0;', GetLoadedAssembly matches the version.",
            () =>
            {
                var rd = LoadRd("/PluginA;v1.0.0.0;component/Themes/Plugin.xaml");
                return (rd.Contains("PluginA.Greeting"), "loaded");
            });

        h.Check("2.5", "Plugin.xaml's merged /SharedControls;component/Themes/Shared.xaml (cross-assembly, unversioned) resolves to A's private SharedControls", Pass,
            "HIGH while only ONE SharedControls is loaded in the process (name-only lookup).",
            () =>
            {
                var rd = LoadRd("/PluginA;component/Themes/Plugin.xaml");
                var t = SharedText(rd);
                return (t == "MARK:SharedControls.v1.SharedDictionary", $"Shared.VersionText = {t}");
            });
    }

    // ------------------------------------------------------------------ 3  (Q3)

    private static void S3(Harness h)
    {
        var a = NewCompartment("A", "PluginA");
        var ea = a.CreateEntry("PluginA");
        var b = NewCompartment("B", "PluginB", eager: true, imports: a);
        var eb = b.CreateEntry("PluginB");
        var aStatusType = a.LoadByName("PluginA").GetType("PluginA.AStatusText", true)!;

        h.Check("3.1", "Own-assembly type (xmlns:local, clr-namespace without assembly=) in plugin A's view", Pass,
            "HIGH. The markup compiler records the full name 'PluginA, Version=1.0.0.0, ...' (verified with XamlAsmInspect); Baml2006SchemaContext.ResolveAssembly -> GetLoadedAssembly finds A's copy.",
            () => ViewShows(h, ea.CreateView, "A-view", "PluginA.AStatusText", "LocalText", el => ReferenceEquals(el.GetType(), aStatusType)));

        h.Check("3.2", "Host-owned type (HostSdk.SharedBanner) in plugin A's view is the default-context type", Pass,
            "HIGH. HostSdk exists once (default context).",
            () => ViewShows(h, ea.CreateView, "A-view-banner", "HostSdk.SharedBanner", "Banner",
                el => el is SharedBanner && AlcOf(el) == "Default"));

        h.Check("3.3", "Another plugin's type (PluginA.AStatusText) in plugin B's view is the SAME Type as compartment A's", Pass,
            "HIGH. BAML resolves 'PluginA, Version=1.0.0.0' via AppDomain-wide GetLoadedAssembly (WPF ignores ALCs) -> A's assembly, which is also what B's compartment returns for PluginA.",
            () => ViewShows(h, eb.CreateView, "B-view", "PluginA.AStatusText", "OtherPluginText", el => ReferenceEquals(el.GetType(), aStatusType)));

        h.Check("3.4", "Plugin B's XAML loads plugin A's dictionary by pack URI (/PluginA;component/Themes/Plugin.xaml) and uses a key from it", Pass,
            "HIGH. Name-only lookup; only one PluginA is loaded.",
            () =>
            {
                var v = eb.CreateView();
                var tb = (TextBlock)v.FindName("OtherPluginGreeting");
                return (tb.Text == "MARK:PluginA.ThemeDictionary", $"OtherPluginGreeting = {tb.Text}");
            });
    }

    // ------------------------------------------------------------------ 4  (Q4)

    private static void S4(Harness h)
    {
        var a = NewCompartment("A", "PluginA");
        var ea = a.CreateEntry("PluginA");

        h.Check("4.1", "Implicit DataTemplate from plugin A's dictionary (merged into Application.Resources) applies to A's view-model", Pass,
            "HIGH. DataType resolves to A's AItemViewModel (single copy); DataTemplateKey uses Type identity; the VM is created by A's code from the same assembly.",
            () =>
            {
                Application.Current.Resources.MergedDictionaries.Add(LoadRd("/PluginA;component/Themes/Plugin.xaml"));
                var cc = new ContentControl { Content = ea.CreateViewModel() };
                return ViewShows(h, () => cc, "vm-app", "PluginA.AItemViewModel.Template");
            });

        h.Check("4.2", "Same, with the dictionary merged into the ContentControl's own Resources (scoped, not app-wide)", Pass,
            "HIGH.",
            () =>
            {
                var cc = new ContentControl { Content = ea.CreateViewModel() };
                cc.Resources.MergedDictionaries.Add(LoadRd("/PluginA;component/Themes/Plugin.xaml"));
                return ViewShows(h, () => cc, "vm-local", "PluginA.AItemViewModel.Template");
            });
    }

    // ------------------------------------------------------------------ 5  (Q5, different AssemblyVersion)

    private static void S5(Harness h)
    {
        var a = NewCompartment("A", "PluginA");
        var ea = a.CreateEntry("PluginA");

        h.Check("5.0", "BEFORE B is loaded: A's UnversionedBadge (unversioned pack URI, the SDK-default codegen) works", Pass,
            "HIGH. Only one SharedControls in the process.",
            () => ViewShows(h, () => CreateShared(a, "UnversionedBadge"), "A-unversioned-before", "SharedControls.v1.UnversionedBadge"));

        var b = NewCompartment("B", "PluginB", eager: true, imports: a);
        var eb = b.CreateEntry("PluginB");

        h.Check("5.1", "A's view shows the v1 VersionBadge (BAML records 'SharedControls, Version=1.0.0.0')", Pass,
            "HIGH. GetLoadedAssembly matches the exact version from the BAML record; VersionBadge's generated URI is '/SharedControls;V1.0.0.0;component/...' because AssemblyVersion is an explicit MSBuild property here.",
            () => ViewShows(h, ea.CreateView, "A-view", "SharedControls.v1.VersionBadge", "Badge", el => el.GetType().Assembly.GetName().Version == new Version(1, 0, 0, 0)));

        h.Check("5.2", "B's view shows the v2 VersionBadge", Pass,
            "HIGH. Same, with 2.0.0.0.",
            () => ViewShows(h, eb.CreateView, "B-view", "SharedControls.v2.VersionBadge", "Badge", el => el.GetType().Assembly.GetName().Version == new Version(2, 0, 0, 0)));

        h.Check("5.3", "A's FancyBox gets the v1 template from v1's Themes/Generic.xaml", Pass,
            "HIGH. SystemResources.LoadDictionary reads <name>.g.resources from the control's actual Assembly object (not by name); TargetType resolves by full name incl. version.",
            () =>
            {
                var v = ea.CreateView();
                var w = h.Show(v, "A-fancy");
                try { var m = Marks((DependencyObject)v.FindName("Fancy")); return (m.Contains("v1.FancyBoxTemplate"), $"Fancy shows [{m}]"); }
                finally { w.Close(); }
            });

        h.Check("5.4", "B's FancyBox gets the v2 template", Pass,
            "HIGH. As 5.3.",
            () =>
            {
                var v = eb.CreateView();
                var w = h.Show(v, "B-fancy");
                try { var m = Marks((DependencyObject)v.FindName("Fancy")); return (m.Contains("v2.FancyBoxTemplate"), $"Fancy shows [{m}]"); }
                finally { w.Close(); }
            });

        h.Check("5.5", "VersionBadge created by code from A's copy (versioned URI ;V1.0.0.0;)", Pass,
            "HIGH. The versioned key 'sharedcontrols1.0.0.0' only matches v1.",
            () => ViewShows(h, () => CreateShared(a, "VersionBadge"), "A-badge-code", "SharedControls.v1.VersionBadge"));

        h.Check("5.6", "UnversionedBadge from B's copy (B = most recently loaded)", Pass,
            "HIGH. Name-only lookup returns the last-loaded SharedControls, which is B's.",
            () => ViewShows(h, () => CreateShared(b, "UnversionedBadge"), "B-unversioned", "SharedControls.v2.UnversionedBadge"));

        h.Check("5.7", "AFTER B is loaded: A's UnversionedBadge (unversioned pack URI)", Fail,
            $"HIGH. ResourceContainer resolves '/SharedControls;component/...' by NAME ONLY: SafeSecurityHelper.GetLoadedAssembly scans AppDomain.GetAssemblies() from the end (last loaded = v2), and the AppDomain.AssemblyLoad handler retargets the cached wrapper to the newest copy. Application.LoadComponent then sees stream assembly (v2) != component assembly (v1) and throws \"...{NotFoundFmt}...\".",
            () => ViewShows(h, () => CreateShared(a, "UnversionedBadge"), "A-unversioned-after", "SharedControls.v1.UnversionedBadge"));

        h.Check("5.8", "A's Plugin.xaml merges /SharedControls;component/Themes/Shared.xaml (unversioned): A should see v1's dictionary", Fail,
            "HIGH. AppendAssemblyVersion only adds the version when the URI names the BAML's OWN assembly; a cross-assembly Source stays name-only -> last-loaded copy (v2).",
            () =>
            {
                var t = SharedText(LoadRd("/PluginA;component/Themes/Plugin.xaml"));
                return (t == "MARK:SharedControls.v1.SharedDictionary", $"Shared.VersionText = {t}");
            });

        h.Check("5.9", "Explicit versioned URIs ;v1.0.0.0; and ;v2.0.0.0; each load their own copy", Pass,
            "HIGH. Version is part of the lookup and the cache key.",
            () =>
            {
                var t1 = SharedText(LoadRd("/SharedControls;v1.0.0.0;component/Themes/Shared.xaml"));
                var t2 = SharedText(LoadRd("/SharedControls;v2.0.0.0;component/Themes/Shared.xaml"));
                return (t1!.Contains(".v1.") && t2!.Contains(".v2."), $"v1 -> {t1}; v2 -> {t2}");
            });

        h.Check("5.10", "Loose XAML (XamlReader.Parse) with clr-namespace:...;assembly=SharedControls resolves to the most recently loaded copy (v2)", Pass,
            "MEDIUM-HIGH. System.Xaml XamlSchemaContext.ResolveAssembly -> GetLoadedAssembly(short name) -> last match in AppDomain.GetAssemblies() (assumed to be load order).",
            () =>
            {
                var cc = (ContentControl)XamlReader.Parse(
                    "<ContentControl xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' " +
                    "xmlns:s='clr-namespace:SharedControls;assembly=SharedControls'><s:VersionBadge/></ContentControl>");
                var badge = cc.Content!;
                return (badge.GetType().Assembly.GetName().Version == new Version(2, 0, 0, 0), Who(badge));
            });
    }

    // ------------------------------------------------------------------ 6  (Q6)

    private static void S6(Harness h)
    {
        var a = NewCompartment("A", "PluginA");
        var ea = a.CreateEntry("PluginA");
        var view = ea.CreateView();
        var w = h.Show(view, "A-view");

        h.Check("6.1", "DynamicResource {x:Static host:HostKeys.AccentBrush} (typed ComponentResourceKey from HostSdk) resolves from Application.Resources", Pass,
            "HIGH. Key lookup by equality; HostSdk is host-owned so ComponentResourceKey's TypeInTargetAssembly is the same Type everywhere.",
            () =>
            {
                var bg = ((Border)view.FindName("AccentBorder")).Background as SolidColorBrush;
                return (bg?.Color == Colors.SteelBlue, $"Background = {bg?.Color}");
            });

        h.Check("6.2", "DynamicResource Host.Title (string key) resolves", Pass, "HIGH.",
            () =>
            {
                var t = ((TextBlock)view.FindName("HostTitleText")).Text;
                return (t == "MARK:HostSdk.Title", $"Text = {t}");
            });

        h.Check("6.3", "Replacing the app-level resource updates the plugin view (it is really dynamic)", Pass, "HIGH.",
            () =>
            {
                Application.Current.Resources[HostKeys.AccentBrush] = new SolidColorBrush(Colors.OrangeRed);
                Pump();
                var bg = ((Border)view.FindName("AccentBorder")).Background as SolidColorBrush;
                return (bg?.Color == Colors.OrangeRed, $"Background = {bg?.Color}");
            });
        w.Close();
    }

    // ------------------------------------------------------------------ 7  (same AssemblyVersion, different content)

    private static void S7(Harness h)
    {
        var a = NewCompartment("A", "PluginA");
        var ea = a.CreateEntry("PluginA");

        h.Check("7.0", "BEFORE C is loaded: A's view (v1 badge) works", Pass, "HIGH.",
            () => ViewShows(h, ea.CreateView, "A-view-before", "SharedControls.v1.VersionBadge"));

        var c = NewCompartment("C", "PluginC");
        var ec = c.CreateEntry("PluginC");

        h.Check("7.1", "C's view shows the v1b badge (C's copy is the most recently loaded)", Pass, "HIGH.",
            () => ViewShows(h, ec.CreateView, "C-view", "SharedControls.v1b.VersionBadge"));

        h.Check("7.2", "AFTER C is loaded: A's view", Fail,
            "HIGH. A's BAML asks for 'SharedControls, Version=1.0.0.0' -> GetLoadedAssembly returns the LAST loaded match = C's v1b -> a v1b VersionBadge is created and A's generated Connect() casts it to A's (v1) VersionBadge field type -> InvalidCastException inside XamlParseException.",
            () => ViewShows(h, ea.CreateView, "A-view-after", "SharedControls.v1.VersionBadge"));

        h.Check("7.3", "AFTER C is loaded: VersionBadge created by code from A's copy (versioned URI ;V1.0.0.0;)", Fail,
            $"HIGH. The version is identical, so the versioned key matches C's copy too; LoadComponent throws \"...{NotFoundFmt}...\".",
            () => ViewShows(h, () => CreateShared(a, "VersionBadge"), "A-badge-code-after", "SharedControls.v1.VersionBadge"));

        h.Check("7.4", "AFTER C is loaded: A's FancyBox (created by code) gets A's generic.xaml template", Fail,
            "MEDIUM. SystemResources loads v1's generic.baml from the right Assembly object, but its {x:Type local:FancyBox} resolves 'SharedControls, Version=1.0.0.0' to C's copy, so the implicit style is keyed by C's FancyBox type and A's FancyBox finds no default style (renders empty).",
            () => ViewShows(h, () => CreateShared(a, "FancyBox"), "A-fancy-after", "SharedControls.v1.FancyBoxTemplate"));
    }

    // ------------------------------------------------------------------ 8  (identical copies in two compartments)

    private static void S8(Harness h)
    {
        var a1 = NewCompartment("A1", "PluginA");
        var e1 = a1.CreateEntry("PluginA");

        h.Check("8.0", "BEFORE A2 exists: A1's view works", Pass, "HIGH.",
            () => ViewShows(h, e1.CreateView, "A1-before", "PluginA.PluginAView"));

        var a2 = NewCompartment("A2", "PluginA");
        var e2 = a2.CreateEntry("PluginA");

        h.Check("8.1", "A2's view (A2 = most recently loaded copy) works", Pass, "HIGH.",
            () => ViewShows(h, e2.CreateView, "A2", "PluginA.PluginAView"));

        h.Check("8.2", "AFTER A2 exists: A1's view (same bytes, other compartment)", Fail,
            $"HIGH. '/PluginA;V1.0.0.0;component/pluginaview.xaml' matches both copies; the newest (A2) wins; Application.LoadComponent: stream assembly (A2) != component assembly (A1) -> \"...{NotFoundFmt}...\".",
            () => ViewShows(h, e1.CreateView, "A1-after", "PluginA.PluginAView"));

        h.Check("8.3", "AFTER A2 exists: A1's view-model gets the implicit DataTemplate from A1's theme URI", Fail,
            "HIGH. The pack URI loads A2's dictionary; DataType resolves to A2's AItemViewModel; A1's VM type differs -> no template (ToString() shown).",
            () =>
            {
                var cc = new ContentControl { Content = e1.CreateViewModel() };
                cc.Resources.MergedDictionaries.Add(LoadRd("/PluginA;component/Themes/Plugin.xaml"));
                return ViewShows(h, () => cc, "A1-vm", "PluginA.AItemViewModel.Template");
            });

        h.Check("8.4", "A2's view-model gets the template", Pass, "HIGH.",
            () =>
            {
                var cc = new ContentControl { Content = e2.CreateViewModel() };
                cc.Resources.MergedDictionaries.Add(LoadRd("/PluginA;component/Themes/Plugin.xaml"));
                return ViewShows(h, () => cc, "A2-vm", "PluginA.AItemViewModel.Template");
            });
    }

    // ------------------------------------------------------------------ 9  (XAML-only reference, not loaded yet)

    private static void S9(Harness h, Mitigation m)
    {
        if (m == Mitigation.DefaultForwarder) Compartment.InstallDefaultContextForwarder();
        var d = NewCompartment("D", "PluginD", eager: m == Mitigation.EagerLoad);
        var ed = d.CreateEntry("PluginD");
        Console.WriteLine($"    SharedControls loaded anywhere before creating the view: {AppDomain.CurrentDomain.GetAssemblies().Any(x => x.GetName().Name == "SharedControls")}");

        Func<FrameworkElement> create = m == Mitigation.ContextualReflection
            ? () => { using (d.EnterContextualReflection()) return ed.CreateView(); }
            : ed.CreateView;

        var (predicted, why) = m switch
        {
            Mitigation.None => (Fail, "HIGH. Nothing has loaded SharedControls yet; GetLoadedAssembly finds nothing; Baml2006SchemaContext falls back to Assembly.Load(...) called from PresentationFramework = the DEFAULT context, which has no SharedControls -> FileNotFoundException inside XamlParseException. WPF never consults the plugin's ALC (no AssemblyLoadContext reference anywhere in dotnet/wpf)."),
            Mitigation.ContextualReflection => (Pass, "MEDIUM-HIGH. Assembly.Load(string) honours AssemblyLoadContext.CurrentContextualReflectionContext, so WPF's fallback load goes to compartment D. Only covers code that runs inside the scope (not later template expansion)."),
            Mitigation.EagerLoad => (Pass, "HIGH. SharedControls is already in AppDomain.GetAssemblies(), GetLoadedAssembly finds it."),
            _ => (Pass, "MEDIUM. The Default.Resolving handler hands WPF's failed default-context load to compartment D."),
        };

        h.Check(h.ScenarioId + ".1", $"Plugin D's view (SharedControls used only from XAML), mitigation={m}", predicted, why,
            () => ViewShows(h, create, "D-view", "SharedControls.v1.VersionBadge"));
    }

    // ------------------------------------------------------------------ 10 (private dependency rolled forward)

    private static void S10(Harness h, Mitigation m)
    {
        if (m == Mitigation.DefaultForwarder) Compartment.InstallDefaultContextForwarder();
        var a = NewCompartment("A", "PluginA-rollforward");
        var ea = a.CreateEntry("PluginA");
        Console.WriteLine($"    SharedControls in compartment A: {a.LoadByName("SharedControls").FullName}");

        Func<FrameworkElement> create = m == Mitigation.ContextualReflection
            ? () => { using (a.EnterContextualReflection()) return ea.CreateView(); }
            : ea.CreateView;

        var (predicted, why) = m switch
        {
            Mitigation.None => (Fail, "HIGH. A's BAML records 'SharedControls, Version=1.0.0.0'. GetLoadedAssembly requires an exact version match when one is given, so the loaded 2.0.0.0 copy is skipped; the Assembly.Load fallbacks (full name, then short name) run in the default context and fail."),
            Mitigation.ContextualReflection => (Pass, "MEDIUM. Assembly.Load(full name) goes to compartment A, whose Load() returns the 2.0.0.0 copy (higher version accepted)."),
            _ => (Pass, "MEDIUM. Default.Resolving forwards to compartment A's 2.0.0.0 copy."),
        };

        h.Check(h.ScenarioId + ".1", $"Plugin A's view with SharedControls rolled forward to 2.0.0.0, mitigation={m}", predicted, why,
            () => ViewShows(h, create, "A-rollforward", "SharedControls.v2.VersionBadge"));
    }

    // ------------------------------------------------------------------ 11 (AppendLocalAssemblyVersionForSourceUri)

    private static void S11(Harness h, bool switchOn)
    {
        var a = NewCompartment("A", "PluginA");
        var b = NewCompartment("B", "PluginB", eager: true, imports: a);
        AppContext.TryGetSwitch("Switch.System.Windows.Baml2006.AppendLocalAssemblyVersionForSourceUri", out bool on);
        Console.WriteLine($"    switch AppendLocalAssemblyVersionForSourceUri = {on}");

        h.Check(h.ScenarioId + ".1", "v1's SelfRef.xaml (loaded with ;v1.0.0.0;) merges its own Shared.xaml unversioned -> should get v1", switchOn ? Pass : Fail,
            switchOn
                ? "HIGH. Baml2006ReaderInternal.CreateTypeConverterMarkupExtension wraps ResourceDictionary.Source; BaseUriHelper.AppendAssemblyVersion adds ';v1.0.0.0' because the URI names the BAML's local assembly."
                : "HIGH. The switch defaults to false (AppContextDefaultValues), so Source stays name-only -> last loaded copy (v2).",
            () =>
            {
                var t = SharedText(LoadRd("/SharedControls;v1.0.0.0;component/Themes/SelfRef.xaml"));
                return (t == "MARK:SharedControls.v1.SharedDictionary", $"Shared.VersionText = {t}");
            });

        h.Check(h.ScenarioId + ".2", "PluginA's Plugin.xaml merging SharedControls' dictionary (cross-assembly) -> should get v1", Fail,
            "HIGH. The switch only helps for the BAML's OWN assembly; cross-assembly Source URIs stay name-only either way.",
            () =>
            {
                var t = SharedText(LoadRd("/PluginA;component/Themes/Plugin.xaml"));
                return (t == "MARK:SharedControls.v1.SharedDictionary", $"Shared.VersionText = {t}");
            });
    }
}

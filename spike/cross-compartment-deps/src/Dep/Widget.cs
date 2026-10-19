using System.Runtime.Loader;
namespace Dep;

[AttributeUsage(AttributeTargets.All)]
public sealed class WidgetMarkerAttribute : Attribute { }

public class Widget
{
    public static string Ver => typeof(Widget).Assembly.GetName().Version!.ToString();
    public static string Alc => AssemblyLoadContext.GetLoadContext(typeof(Widget).Assembly)?.Name ?? "?";
    public virtual string Describe() => $"Widget(Dep {Ver} @ALC '{Alc}')";
#if V1
    public string OldApi() => "OldApi (v1 only)";
#else
    public string NewApi()
    {
        var t = GetType(); while (t.Name != "Widget") t = t.BaseType!;
        var objAlc = AssemblyLoadContext.GetLoadContext(t.Assembly)?.Name;
        return $"NewApi (v2+): code from Dep {Ver} @'{Alc}' running on a {GetType().Name} whose Widget base is Dep {t.Assembly.GetName().Version} @'{objAlc}'"
             + (t == typeof(Widget) ? "" : "  <-- TYPE-SAFETY HOLE: method of a different Widget type ran on this object");
    }
#endif
#if V3
    public string V3Api() => "V3Api (v3 only)";
#endif
}

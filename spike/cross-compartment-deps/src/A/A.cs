using Contracts;
using Dep;
namespace A;

// ---------- Non-leaky public API (only A's own types, contracts and BCL) ----------
public interface IAService { string Name { get; } string Invoke(IACallback cb); }
public interface IACallback { string OnCall(string input); }
public interface IHandler<T> { string Handle(T item); }

public class AService : IAService
{
    public string Name => "AService";
    public string Invoke(IACallback cb) => "A.Invoke -> " + cb.OnCall("from A");
    public static IAService Create() => new AService();
    public static object CreateAsObject() => new AService();
    public static string Accept(IAService s) =>
        s is AService ? "A recognised its own AService instance" : $"A accepted foreign IAService {s.GetType().FullName} ({Where.Of(s.GetType())}): {s.Name}";
}

public class Box<T>(T value) { public T Value { get; } = value; }

public static class AGenerics
{
    public static Box<T> Wrap<T>(T v) => new(v);
    public static string Describe<T>(Box<T> b) => $"Box<{typeof(T).FullName} from {Where.Of(typeof(T))}> holding '{b.Value}'";
    public static string RunHandler<T>(IHandler<T> h, T item) => h.Handle(item);
    public static List<Box<string>> Many() => [new("x"), new("y")];
}

public class AException(string m) : Exception(m);
public static class AThrower { public static void Throw() => throw new AException("boom from A"); }

public class ABase { public virtual string Who() => "ABase"; public static string CallWho(ABase b) => b.Who(); }

public class AGreeter : IGreeter { public string Greet() => $"hello from A ({Where.Of(typeof(AGreeter))})"; }

// ---------- Leaky public API: exposes Dep (compartment-private v2 in X) ----------
public static class AWidgets
{
    public static Widget Make() => new();                         // leak: return type
    public static List<Widget> MakeMany() => [new(), new()];      // leak: generic argument
    public static string Inspect(Widget w) =>                     // leak: parameter type
        $"A.Inspect({w.Describe()}); A's Widget is {Where.Of(typeof(Widget))}";
    public static object MakeAsObject() => new Widget();          // no leak in signature
    public static string InspectObject(object o) => o is Widget w // no leak in signature
        ? $"A: object IS my Widget: {w.Describe()}"
        : $"A: object is NOT my Widget! it is {o.GetType().FullName} from {Where.Of(o.GetType())}; mine is {Where.Of(typeof(Widget))}";
    public static Func<Widget, string> Describer => w => w.Describe(); // leak: delegate generic arg

    internal static Widget InternalOnly() => new();               // not leaked (internal)
    private static Widget? s_hidden;                              // not leaked (private)
    public static string UseHidden() { s_hidden ??= InternalOnly(); return s_hidden.Describe(); }
}

public class SpecialWidget : Widget { public override string Describe() => "Special" + base.Describe(); } // leak: base type

[WidgetMarker] public class Decorated { }                         // leak (weak): custom attribute
public interface IWidgetSource : IHandler<Widget> { }             // leak: interface generic arg
public class Constrained<T> where T : Widget { }                  // leak: generic constraint
public class Holder { public Widget? Field; protected Widget? Prot; public event Action<Widget>? Changed; public Widget? Prop { get; set; } }

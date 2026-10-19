using System.Runtime.CompilerServices;
namespace PluginA;
public static class A
{
    public static string Self() => "A ok (no D needed)";
    [MethodImpl(MethodImplOptions.NoInlining)] public static string UseD() => DLib.Info.Describe();
    [MethodImpl(MethodImplOptions.NoInlining)] public static string UseDLater() => "later: " + DLib.Info.Describe();
    // JIT compiles the whole method, including the branch that is never taken.
    [MethodImpl(MethodImplOptions.NoInlining)] public static string MaybeD(bool use) => use ? DLib.Info.Describe() : "branch not taken";
    public static string NewThing() { var b = new ADerived(); return b.Who(); }
}
public class ADerived : DLib.BaseThing { }
public class AWithParam { public void M(DLib.Thing t) { } public string Plain() => "plain"; }

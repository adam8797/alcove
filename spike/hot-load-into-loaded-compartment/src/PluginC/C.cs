using System.Runtime.CompilerServices;
namespace PluginC;
public static class C
{
    public static string Self() => "C ok";
    [MethodImpl(MethodImplOptions.NoInlining)] public static string UseE() => ELib.E.Hello();
    [MethodImpl(MethodImplOptions.NoInlining)] public static string UseEOther() => "other: " + ELib.E.Hello();
}

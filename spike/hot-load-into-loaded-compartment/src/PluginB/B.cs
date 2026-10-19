using System.Runtime.CompilerServices;
namespace PluginB;
public static class B
{
    [MethodImpl(MethodImplOptions.NoInlining)] public static string UseD() => DLib.Info.Describe();
    [MethodImpl(MethodImplOptions.NoInlining)] public static string UseNewIn2() => DLib.Info.NewIn2() + " / " + DLib.Info.Describe();
}

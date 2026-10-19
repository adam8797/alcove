using System.Runtime.CompilerServices;
namespace PluginB1;
public static class B1 { [MethodImpl(MethodImplOptions.NoInlining)] public static string UseD() => DLib.Info.Describe(); }

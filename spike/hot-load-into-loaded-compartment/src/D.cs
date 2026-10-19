using System.Runtime.Loader;
namespace DLib;
public static class Info
{
#if V2
    public const string Version = "2.0";
    public static string NewIn2() => "NewIn2 present";
#else
    public const string Version = "1.0";
#endif
    // Not a const at call sites: we want the *runtime* answer, so read it via a method.
    public static string Describe()
    {
        var asm = typeof(Info).Assembly;
        return $"D {RuntimeVersion()} (asmver {asm.GetName().Version}) in ALC '{AssemblyLoadContext.GetLoadContext(asm)?.Name}'";
    }
    public static string RuntimeVersion() => Version;
}
public class BaseThing { public virtual string Who() => "BaseThing from D " + Info.RuntimeVersion(); }
public class Thing { }

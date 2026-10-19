using System.Runtime.Loader;

namespace FixtureDep;

/// <summary>Which copy of the dependency is running: version and load context.</summary>
public static class Info
{
#if V3
    public const string Version = "3.0";
#elif V2
    public const string Version = "2.0";
#else
    public const string Version = "1.0";
#endif

    // Read through a method so call sites see the runtime answer, not a compiled-in constant.
    public static string RuntimeVersion() => Version;

    public static string Context => AssemblyLoadContext.GetLoadContext(typeof(Info).Assembly)?.Name ?? "?";

    public static string Describe() => $"Dep {RuntimeVersion()} in '{Context}'";

#if V2 || V3
    public static string NewIn2() => "NewIn2 present";
#endif
}

public class BaseThing
{
    public virtual string Who() => "BaseThing from Dep " + Info.RuntimeVersion();
}

/// <summary>A type a provider plugin exposes in its public API.</summary>
public class Widget
{
    public virtual string Describe() => $"Widget(Dep {Info.RuntimeVersion()} in '{Info.Context}')";

#if V3
    public string V3Api() => "V3Api present";
#endif
}

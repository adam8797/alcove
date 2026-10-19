using System.Reflection;
using System.Runtime.Loader;
namespace Contracts;

public interface IGreeter { string Greet(); }

public sealed record ScenarioResult(string Name, bool Ok, string Detail);

/// <summary>Each test plugin exposes "&lt;Asm&gt;.Scenarios : IScenarioSource".</summary>
public interface IScenarioSource { IEnumerable<ScenarioResult> Run(); }

public static class Where
{
    public static string Ctx(Assembly a) => AssemblyLoadContext.GetLoadContext(a)?.Name ?? "<none>";
    public static string Of(Type t) => $"{t.Assembly.GetName().Name} {t.Assembly.GetName().Version} @ALC '{Ctx(t.Assembly)}'";
}

/// <summary>Runs one step, recording whether the step's inner method body was entered
/// (if not, the failure happened while JIT-compiling / binding the inner method).</summary>
public static class Step
{
    [ThreadStatic] public static bool Entered;
    public static ScenarioResult Run(string name, Func<string> body)
    {
        Entered = false;
        try { return new(name, true, body()); }
        catch (Exception e)
        {
            var when = Entered ? "at CALL time (inner method body was running)" : "at JIT/bind time (inner method body never started)";
            return new(name, false, $"{e.GetType().FullName}: {e.Message} -- {when}");
        }
    }
}

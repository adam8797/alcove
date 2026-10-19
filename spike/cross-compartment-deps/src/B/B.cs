using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using A;
using Contracts;
namespace B;

public class BThing { public override string ToString() => "BThing"; }
public class BCallback : IACallback { public string OnCall(string input) => $"B callback got '{input}'"; }
public class BService : IAService { public string Name => "BService"; public string Invoke(IACallback cb) => cb.OnCall("from B"); }
public class BHandler : IHandler<BThing> { public string Handle(BThing item) => $"B handled {item}"; }
public class BDerived : ABase { public override string Who() => "BDerived (overrides A's virtual)"; }
public class BGreeter : IGreeter { public string Greet() => $"hello from B ({Where.Of(typeof(BGreeter))})"; }

/// <summary>Public API of B that exposes A's types (used by C for chain/diamond tests).</summary>
public static class BApi
{
    public static IAService GetService() => AService.Create();
    public static Type AServiceType => typeof(AService);
    public static string UseService(IAService s) => "B used " + s.Name + " -> " + AService.Accept(s);
    public static Box<T> Wrap<T>(T v) => AGenerics.Wrap(v);
    public static string HelloViaA() => "B says: " + AService.Create().Invoke(new BCallback());
}

public class Scenarios : IScenarioSource
{
    public IEnumerable<ScenarioResult> Run()
    {
        // ---- Q1: types of A used from B ----
        yield return Step.Run("Q1 A's type lives in", () => Where.Of(typeof(AService)));
        yield return Step.Run("Q1 call A static, get IAService, cast to AService", Q1Cast);
        yield return Step.Run("Q1 object-typed A instance cast to AService", () => { Step.Entered = true; var o = AService.CreateAsObject(); return $"is AService: {o is AService}; cast ok: {((AService)o).Name}"; });
        yield return Step.Run("Q1 B implements A's callback interface, A calls back", () => AService.Create().Invoke(new BCallback()));
        yield return Step.Run("Q1 B implements IAService, passes to A", () => AService.Accept(new BService()));
        yield return Step.Run("Q1 A generic Box<BThing> via A's generic method", () => AGenerics.Describe(AGenerics.Wrap(new BThing())));
        yield return Step.Run("Q1 B constructs Box<BThing> itself, passes to A", () => AGenerics.Describe(new Box<BThing>(new BThing())));
        yield return Step.Run("Q1 B implements IHandler<BThing>, A invokes it", () => AGenerics.RunHandler(new BHandler(), new BThing()));
        yield return Step.Run("Q1 List<Box<string>> from A", () => string.Join(",", AGenerics.Many().Select(b => b.Value)));
        yield return Step.Run("Q1 typeof(Box<>) in B == A's own Box<>", () => { var mine = typeof(Box<>); var theirs = typeof(AService).Assembly.GetType("A.Box`1")!; return $"equal: {mine == theirs}"; });
        yield return Step.Run("Q1 catch A's exception type", () => { try { AThrower.Throw(); return "no throw?!"; } catch (AException e) { return "caught AException: " + e.Message; } });
        yield return Step.Run("Q1 B subclasses A's class, A calls override", () => ABase.CallWho(new BDerived()));

        // ---- Q2a: B never references Dep; uses only object-typed access to A's Dep objects ----
        yield return Step.Run("Q2a B's assembly references", () => string.Join(", ", typeof(Scenarios).Assembly.GetReferencedAssemblies().Select(a => a.Name)));
        yield return Step.Run("Q2a object round trip of A's Widget", () => AWidgets.InspectObject(AWidgets.MakeAsObject()));
        yield return Step.Run("Q2a A's private Dep usage", () => AWidgets.UseHidden());

        // ---- Q5: reflection APIs from inside B (caller is in compartment Y) ----
        yield return Step.Run("Q5 GetLoadContext(A) from B", () => $"{Where.Ctx(typeof(AService).Assembly)}; B's own: {Where.Ctx(typeof(Scenarios).Assembly)}");
        yield return Step.Run("Q5 Type.GetType(\"A.AService, A\") from B", () => { var t = Type.GetType("A.AService, A"); return t is null ? "null" : $"{Where.Of(t)}; same as typeof: {t == typeof(AService)}"; });
        yield return Step.Run("Q5 Type.GetType(generic with B arg) from B", () => { var t = Type.GetType("A.Box`1[[B.BThing, B]], A"); return t is null ? "null" : $"{t}; same as typeof: {t == typeof(Box<BThing>)}"; });
        yield return Step.Run("Q5 Assembly.Load(\"A\") from B", () => { var a = Assembly.Load("A"); return $"{Where.Ctx(a)}; same instance: {a == typeof(AService).Assembly}"; });
        yield return Step.Run("Q5 Activator.CreateInstance(\"A\",\"A.AService\") from B", () => { var o = Activator.CreateInstance("A", "A.AService")!.Unwrap(); return $"is AService: {o is AService}"; });
        yield return Step.Run("Q5 Activator.CreateInstance(typeof(Box<BThing>), ...)", () => { var o = Activator.CreateInstance(typeof(Box<BThing>), new BThing()); return $"is Box<BThing>: {o is Box<BThing>}"; });
        yield return Step.Run("Q5 AssemblyLoadContext.CurrentContextualReflectionContext", () => AssemblyLoadContext.CurrentContextualReflectionContext?.Name ?? "null");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static string Q1Cast() { Step.Entered = true; IAService s = AService.Create(); var a = (AService)s; return $"cast ok, Name={a.Name}"; }
}

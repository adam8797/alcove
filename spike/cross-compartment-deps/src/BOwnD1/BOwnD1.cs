using System.Runtime.CompilerServices;
using A;
using Contracts;
using Dep;
namespace BOwnD1;

/// <summary>B variant compiled against Dep v1 for its OWN use; never touches A's leaky members.</summary>
public class Scenarios : IScenarioSource
{
    public IEnumerable<ScenarioResult> Run()
    {
        yield return Step.Run("Dep as seen by B", () => Where.Of(typeof(Widget)));
        yield return Step.Run("non-leaky A API", NonLeaky);
        yield return Step.Run("B's own Widget.OldApi()  [v1-only member]", Old);
        yield return Step.Run("A.InspectObject(B's Widget)  [object-typed exchange]", Exchange);
        yield return Step.Run("A's internal Dep usage", () => AWidgets.UseHidden());
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static string NonLeaky() { Step.Entered = true; return AService.Create().Invoke(new Cb()); }
    [MethodImpl(MethodImplOptions.NoInlining)] static string Old() { Step.Entered = true; return new Widget().OldApi(); }
    [MethodImpl(MethodImplOptions.NoInlining)] static string Exchange() { Step.Entered = true; return AWidgets.InspectObject(new Widget()); }
    class Cb : IACallback { public string OnCall(string s) => "BOwnD1 cb: " + s; }
}

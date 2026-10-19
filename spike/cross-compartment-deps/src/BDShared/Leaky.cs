using System.Runtime.CompilerServices;
using A;
using Contracts;
using Dep;
#if BD3
namespace BD3;
#else
namespace BD2;
#endif

/// <summary>B variant that references Dep and uses A's leaky API (Dep.Widget in signatures).</summary>
public class Scenarios : IScenarioSource
{
    public IEnumerable<ScenarioResult> Run()
    {
        yield return Step.Run("Dep as seen by B", () => Where.Of(typeof(Widget)));
        yield return Step.Run("non-leaky A API", NonLeaky);
        yield return Step.Run("A.Make().Describe()  [Widget return type]", Make);
        yield return Step.Run("A.Inspect(new Widget())  [Widget parameter]", Inspect);
        yield return Step.Run("A.MakeMany()  [List<Widget>]", MakeMany);
        yield return Step.Run("A.Describer(new Widget())  [Func<Widget,string> property]", Describer);
        yield return Step.Run("(Widget)A.MakeAsObject()  [cast]", CastFromObject);
        yield return Step.Run("A.InspectObject(new Widget())  [object-typed, A does 'is Widget']", InspectObject);
        yield return Step.Run("new SpecialWidget().NewApi()  [A type derived from Dep type]", Special);
        yield return Step.Run("B's own Widget.NewApi()", () => { Step.Entered = true; return new Widget().NewApi(); });
#if BD3
        yield return Step.Run("B's own Widget.V3Api()  [needs Dep v3]", V3);
#endif
    }

    [MethodImpl(MethodImplOptions.NoInlining)] static string NonLeaky() { Step.Entered = true; return AService.Create().Name; }
    [MethodImpl(MethodImplOptions.NoInlining)] static string Make() { Step.Entered = true; return AWidgets.Make().Describe(); }
    [MethodImpl(MethodImplOptions.NoInlining)] static string Inspect() { Step.Entered = true; return AWidgets.Inspect(new Widget()); }
    [MethodImpl(MethodImplOptions.NoInlining)] static string MakeMany() { Step.Entered = true; return string.Join(" | ", AWidgets.MakeMany().Select(w => w.Describe())); }
    [MethodImpl(MethodImplOptions.NoInlining)] static string Describer() { Step.Entered = true; return AWidgets.Describer(new Widget()); }
    [MethodImpl(MethodImplOptions.NoInlining)] static string CastFromObject() { Step.Entered = true; var w = (Widget)AWidgets.MakeAsObject(); return "cast ok: " + w.Describe(); }
    [MethodImpl(MethodImplOptions.NoInlining)] static string InspectObject() { Step.Entered = true; return AWidgets.InspectObject(new Widget()); }
    [MethodImpl(MethodImplOptions.NoInlining)] static string Special() { Step.Entered = true; return new SpecialWidget().NewApi(); }
#if BD3
    [MethodImpl(MethodImplOptions.NoInlining)] static string V3() { Step.Entered = true; return new Widget().V3Api(); }
#endif
}

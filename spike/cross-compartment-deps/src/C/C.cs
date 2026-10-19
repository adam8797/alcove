using System.Runtime.CompilerServices;
using A;
using B;
using Contracts;
namespace C;

public class CThing { public override string ToString() => "CThing"; }
public class CCallback : IACallback { public string OnCall(string s) => "C callback got '" + s + "'"; }

/// <summary>C references both A (directly) and B (which exposes A's types): the diamond C->A, C->B->A.</summary>
public class Scenarios : IScenarioSource
{
    public IEnumerable<ScenarioResult> Run()
    {
        yield return Step.Run("A's type as seen by C", () => Where.Of(typeof(AService)));
        yield return Step.Run("B's type as seen by C", () => Where.Of(typeof(BApi)));
        yield return Step.Run("typeof(AService) in C == B's typeof(AService)", Same);
        yield return Step.Run("IAService from B -> A.Accept (C->B->A object to C->A call)", ViaB);
        yield return Step.Run("A object created by C -> B.UseService", ToB);
        yield return Step.Run("C callback -> A.Invoke (object from B)", Callback);
        yield return Step.Run("B.Wrap<CThing> -> A.Describe", Generic);
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static string Same() { Step.Entered = true; return (typeof(AService) == BApi.AServiceType).ToString(); }
    [MethodImpl(MethodImplOptions.NoInlining)] static string ViaB() { Step.Entered = true; return AService.Accept(BApi.GetService()); }
    [MethodImpl(MethodImplOptions.NoInlining)] static string ToB() { Step.Entered = true; return BApi.UseService(new AService()); }
    [MethodImpl(MethodImplOptions.NoInlining)] static string Callback() { Step.Entered = true; return BApi.GetService().Invoke(new CCallback()); }
    [MethodImpl(MethodImplOptions.NoInlining)] static string Generic() { Step.Entered = true; return AGenerics.Describe(BApi.Wrap(new CThing())); }
}

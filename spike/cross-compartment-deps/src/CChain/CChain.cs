using Contracts;
using B;
namespace CChain;
/// <summary>C references only B; B uses A internally (pure chain C->B->A).</summary>
public class Scenarios : IScenarioSource
{
    public IEnumerable<ScenarioResult> Run()
    {
        yield return Step.Run("CChain assembly references", () => string.Join(", ", typeof(Scenarios).Assembly.GetReferencedAssemblies().Select(a => a.Name)));
        yield return Step.Run("B.HelloViaA()", () => BApi.HelloViaA());
    }
}

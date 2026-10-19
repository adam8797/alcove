using Alcove.Internal.Cells;
using Alcove.Internal.Install;
using NuGet.Versioning;

namespace Alcove.Tests.Install;

/// <summary>The hot-load rule (architecture §6): a name is OK if never answered, only failed, or answered identically.</summary>
public sealed class HotLoadRuleTests
{
    private static readonly NuGetVersion V1 = NuGetVersion.Parse("1.0.0");
    private static readonly NuGetVersion V2 = NuGetVersion.Parse("2.0.0");

    [Fact]
    public void NamesNeverAnsweredOnlyFailedOrAnsweredIdentically_AreOk()
    {
        var committed = Committed(
            ("Same", CellPlan.PrivateAnswer("/d/1.0.0/Same.dll", "Same", V1, AnswerReason.CellPrivate)),
            ("Failed", CellAnswer.Failed("not found")),
            ("Shared", CellAnswer.Host(AnswerReason.Unknown)));
        var plan = new CellPlan();
        plan.Private["Same"] = new PrivateAssembly("Same", "/d/1.0.0/Same.dll", "Same", V1);
        plan.Private["Failed"] = new PrivateAssembly("Failed", "/f/1.0.0/Failed.dll", "Failed", V1);
        plan.Private["New"] = new PrivateAssembly("New", "/n/1.0.0/New.dll", "New", V1);
        plan.HostShared.Add("Shared");   // host answers are the same whatever the reason

        Assert.Empty(HotLoadRule.Check("C", committed, plan));
    }

    [Fact]
    public void ADifferentAnswerForACommittedName_IsAConflict()
    {
        var committed = Committed(
            ("FromHost", CellAnswer.Host(AnswerReason.HostShared)),
            ("Private", CellPlan.PrivateAnswer("/p/1.0.0/Private.dll", "Private", V1, AnswerReason.CellPrivate)),
            ("Provided", new CellAnswer(AnswerSource.Provider, AnswerReason.PluginProvided) { ProviderCell = "X" }));
        var plan = new CellPlan();
        plan.Private["FromHost"] = new PrivateAssembly("FromHost", "/h/2.0.0/FromHost.dll", "FromHost", V2);
        plan.Private["Private"] = new PrivateAssembly("Private", "/p/2.0.0/Private.dll", "Private", V2);
        plan.Provided["Provided"] = "Y";

        var conflicts = HotLoadRule.Check("C", committed, plan);

        Assert.Equal(["FromHost", "Private", "Provided"], conflicts.Select(c => c.Name));
        Assert.All(conflicts, c => Assert.Equal("C", c.Cell));
        Assert.Equal(AnswerSource.Host, conflicts[0].Committed.Source);
        Assert.Equal(V2, conflicts[0].Needed.PackageVersion);
    }

    [Fact]
    public void ANativeLibraryFromAnotherPath_IsAConflict()
    {
        var committed = new CommittedAnswers(
            new Dictionary<string, CellAnswer>(),
            new Dictionary<string, CellAnswer> { ["libnat.so"] = CellPlan.PrivateAnswer("/n/1.0.0/runtimes/linux-x64/native/libnat.so", "Nat", V1, AnswerReason.CellPrivate) });
        var plan = new CellPlan();
        plan.NativeLibraries["libnat.so"] = new NativeLibraryAsset("libnat.so", "/n/2.0.0/runtimes/linux-x64/native/libnat.so", "Nat", V2);

        Assert.Equal("libnat.so", Assert.Single(HotLoadRule.Check("C", committed, plan)).Name);
    }

    private static CommittedAnswers Committed(params (string Name, CellAnswer Answer)[] answers) =>
        new(answers.ToDictionary(a => a.Name, a => a.Answer, StringComparer.OrdinalIgnoreCase), new Dictionary<string, CellAnswer>());
}

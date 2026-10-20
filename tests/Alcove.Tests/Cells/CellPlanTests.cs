using Alcove.Cells;
using Alcove.Hosting;
using NuGet.Versioning;

namespace Alcove.Tests.Cells;

public sealed class CellPlanTests
{
    private static readonly NuGetVersion V1 = NuGetVersion.Parse("1.0.0");

    [Fact]
    public void SameAs_ComparesSourceAndForPrivateAnswersTheExactFile()
    {
        var host = CellAnswer.Host(AnswerReason.HostShared);
        var a = CellPlan.PrivateAnswer("/p/d/1.0.0/D.dll", "D", V1, AnswerReason.CellPrivate);

        Assert.True(host.SameAs(CellAnswer.Host(AnswerReason.Unknown)));   // the reason isn't part of identity
        Assert.True(a.SameAs(a with { Reason = AnswerReason.SharedWpfRelevant }));
        Assert.False(a.SameAs(a with { Path = "/p/d/2.0.0/D.dll", PackageVersion = NuGetVersion.Parse("2.0.0") }));
        Assert.False(a.SameAs(host));
        var x = new CellAnswer(AnswerSource.Provider, AnswerReason.PluginProvided) { ProviderCell = "X" };
        Assert.True(x.SameAs(x with { }));
        Assert.False(x.SameAs(x with { ProviderCell = "Y" }));
    }

    [Fact]
    public void PlannedAnswers_FollowTheResolutionPrecedence()
    {
        var plan = new CellPlan();
        plan.HostShared.Add("Shared");
        plan.HostShared.Add("Both");
        plan.Private["Both"] = new PrivateAssembly("Both", "/both.dll", "Both", V1);
        plan.Provided["Exposed"] = "X";
        plan.HostOwned.Add("Owned");
        plan.Private["Owned"] = new PrivateAssembly("Owned", "/owned.dll", "Owned", V1);

        var answers = plan.GetPlannedAssemblyAnswers();

        Assert.Equal(AnswerSource.Host, answers["Shared"].Source);
        Assert.Equal(AnswerSource.Private, answers["Both"].Source);
        Assert.Equal("X", answers["Exposed"].ProviderCell);
        Assert.Equal(AnswerReason.HostOwned, answers["Owned"].Reason);
    }

    [Fact]
    public void Cell_CommittedAnswersBeforeAnyLoad_AreThePlan_AndMergePlanMovesNamesBetweenCategories()
    {
        var environment = new CellEnvironment(new NoHost());
        var plan = new CellPlan();
        plan.HostShared.Add("D");
        plan.NativeLibraries["libnat.so"] = new NativeLibraryAsset("libnat.so", "/v1/libnat.so", "Nat", V1);
        var cell = environment.CreateCell("plan", plan);
        plan.HostShared.Add("AddedAfterCreation");   // the cell keeps its own copy

        var before = cell.GetCommittedAnswers();
        Assert.Equal(AnswerReason.HostShared, before.Assemblies["D"].Reason);
        Assert.False(before.Assemblies.ContainsKey("AddedAfterCreation"));
        Assert.Equal("/v1/libnat.so", before.NativeLibraries["libnat.so"].Path);

        var update = new CellPlan();
        update.Private["D"] = new PrivateAssembly("D", "/d/2.0.0/D.dll", "D", NuGetVersion.Parse("2.0.0"));
        update.Plugins.Add("New.Plugin");
        cell.MergePlan(update);

        var after = cell.GetCommittedAnswers();
        Assert.Equal(AnswerSource.Private, after.Assemblies["D"].Source);
        Assert.Equal(["New.Plugin"], cell.Plugins);
        Assert.Same(cell, environment.FindCell("plan"));
        Assert.Null(environment.FindCell("other"));
    }

    private sealed class NoHost : IHostAssemblies
    {
        public bool IsHostOwnedAssembly(string assemblyName) => false;

        public bool IsHostSharedAssembly(string assemblyName) => false;
    }
}

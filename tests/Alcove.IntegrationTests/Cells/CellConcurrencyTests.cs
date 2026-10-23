using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using Alcove.Cells;
using Alcove.IntegrationTests.Infrastructure;
using NuGet.Versioning;

namespace Alcove.IntegrationTests.Cells;

/// <summary>
/// Cells load from many threads at once (hosts do, and plugin initialisation runs in parallel): the Gate is
/// only held to decide and freeze answers, never during the load itself.
/// </summary>
[Collection(CellFixturesCollection.Name)]
public sealed class CellConcurrencyTests(CellFixtures fixtures)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    private const string Dep = "Fixture.Dep";

    [Fact]
    public async Task ManyThreadsAcrossCellsAndDelegation_NoDeadlock_OneAssemblyPerNamePerCell()
    {
        const int ConsumerCells = 4;
        const int UserCells = 3;
        const int Threads = 24;
        const int Rounds = 20;
        var (environment, _) = CellTestHelpers.CreateEnvironment();

        // X provides Provider and its exposed Dep 2.0; Y_i delegate both to X; Z_i hold their own Dep 2.0.
        var x = new CellPlan();
        AddPrivate(x, "Fixture.Provider");
        x.Private[Dep] = DepAsset(2);
        environment.CreateCell("X", x);
        var consumers = Enumerable.Range(0, ConsumerCells).Select(i =>
        {
            var plan = new CellPlan();
            AddPrivate(plan, "Fixture.Consumer2");
            plan.Provided["Fixture.Provider"] = "X";
            plan.Provided[Dep] = "X";
            return environment.CreateCell($"Y{i}", plan);
        }).ToList();
        var users = Enumerable.Range(0, UserCells).Select(i =>
        {
            var plan = new CellPlan();
            AddPrivate(plan, "Fixture.UserB");
            plan.Private[Dep] = DepAsset(2);
            return environment.CreateCell($"Z{i}", plan);
        }).ToList();

        var loaded = new ConcurrentDictionary<(string Cell, string Name), ConcurrentBag<Assembly>>();
        var results = new ConcurrentBag<string>();
        using var start = new Barrier(Threads);
        var workers = Enumerable.Range(0, Threads).Select(t => Task.Factory.StartNew(() =>
        {
            start.SignalAndWait(Timeout);
            for (var round = 0; round < Rounds; round++)
            {
                // Each thread walks the cells in its own order, so requests for one name in one cell race.
                var consumer = consumers[(t + round) % ConsumerCells];
                var user = users[(t * 7 + round) % UserCells];
                foreach (var (cell, name) in (t % 2 == 0)
                    ? new[] { (consumer, "Fixture.Consumer2"), (consumer, Dep), (user, "Fixture.UserB"), (user, Dep) }
                    : [(user, Dep), (user, "Fixture.UserB"), (consumer, Dep), (consumer, "Fixture.Consumer2")])
                {
                    loaded.GetOrAdd((cell.Name, name), _ => []).Add(cell.LoadFromAssemblyName(new AssemblyName(name)));
                }

                var consumerAssembly = consumer.LoadFromAssemblyName(new AssemblyName("Fixture.Consumer2"));
                results.Add(CellTestHelpers.Call(consumerAssembly, "FixtureConsumer2.Consumer2", "InspectOwnWidget"));
                results.Add(CellTestHelpers.Call(consumerAssembly, "FixtureConsumer2.Consumer2", "ServiceName"));
                var userAssembly = user.LoadFromAssemblyName(new AssemblyName("Fixture.UserB"));
                results.Add(CellTestHelpers.Call(userAssembly, "FixtureUserB.UserB", "UseD") + " @" + user.Name);
            }
        }, TestContext.Current.CancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

        var all = Task.WhenAll(workers);
        Assert.Same(all, await Task.WhenAny(all, Task.Delay(Timeout, TestContext.Current.CancellationToken)));   // no deadlock
        await all;

        // One instance per name per cell; the delegated Dep is X's in every consumer cell.
        foreach (var ((cell, name), instances) in loaded)
        {
            Assert.Single(instances.Distinct());
        }

        var xDep = environment.FindCell("X")!.Assemblies.Single(a => a.GetName().Name == Dep);
        Assert.All(consumers, c => Assert.Same(xDep, Assert.Single(loaded[(c.Name, Dep)].Distinct())));
        Assert.All(users, u => Assert.NotSame(xDep, loaded[(u.Name, Dep)].First()));
        foreach (var cell in environment.Cells)
        {
            Assert.All(cell.Assemblies.GroupBy(a => a.GetName().Name), g => Assert.Single(g));
            Assert.DoesNotContain(cell.GetCommittedAnswers().Assemblies.Values, a => a.Source == AnswerSource.Failed);
        }

        Assert.DoesNotContain(environment.FindCell("Y0")!.Assemblies, a => a.GetName().Name is Dep or "Fixture.Provider");
        Assert.All(results, r => Assert.StartsWith("OK: ", r));
        Assert.Contains("OK: IS my Widget: Widget(Dep 2.0 in 'X')", results);
        Assert.Equal(1 + 1 + UserCells, results.Distinct().Count());   // identical answers from every thread
    }

    [Fact]
    public void TheGateIsNotHeldWhileLoading()
    {
        var (environment, _) = CellTestHelpers.CreateEnvironment();
        var checkedLoads = new ConcurrentBag<(string Cell, string Name, bool GateHeld)>();
        environment.LoadingHook = (cell, name) => checkedLoads.Add((cell.Name, name, environment.Gate.IsHeldByCurrentThread));
        var x = new CellPlan();
        AddPrivate(x, "Fixture.Provider");
        x.Private[Dep] = DepAsset(2);
        environment.CreateCell("X", x);
        var y = new CellPlan();
        AddPrivate(y, "Fixture.Consumer2");
        y.Provided["Fixture.Provider"] = "X";
        y.Provided[Dep] = "X";
        var consumer = environment.CreateCell("Y", y).LoadFromAssemblyName(new AssemblyName("Fixture.Consumer2"));

        Assert.Equal("OK: IS my Widget: Widget(Dep 2.0 in 'X')", CellTestHelpers.Call(consumer, "FixtureConsumer2.Consumer2", "InspectOwnWidget"));

        // Y's own load, Y delegating Provider and Dep, and X loading them: none under the Gate.
        Assert.Contains(("Y", "Fixture.Consumer2", false), checkedLoads);
        Assert.Contains(("Y", "Fixture.Provider", false), checkedLoads);
        Assert.Contains(("X", "Fixture.Provider", false), checkedLoads);
        Assert.Contains(("X", Dep, false), checkedLoads);
        Assert.DoesNotContain(checkedLoads, c => c.GateHeld);
    }

    [Fact]
    public async Task ALoadWaitingOnAnotherThreadsLoad_DoesNotDeadlock()
    {
        // Under the old design (Gate held across the load), this deadlocks: while cell A loads, it waits for
        // another thread that needs the Gate to load into cell B.
        var (environment, _) = CellTestHelpers.CreateEnvironment();
        var a = new CellPlan();
        AddPrivate(a, "Fixture.UserA");
        a.Private[Dep] = DepAsset(1);
        var b = new CellPlan();
        AddPrivate(b, "Fixture.UserB");
        b.Private[Dep] = DepAsset(2);
        var cellA = environment.CreateCell("A", a);
        var cellB = environment.CreateCell("B", b);
        var otherThread = default(Task<Assembly>);
        environment.LoadingHook = (cell, name) =>
        {
            if (cell == cellA && name == "Fixture.UserA")
            {
                otherThread = Task.Run(() => cellB.LoadFromAssemblyName(new AssemblyName("Fixture.UserB")));
                Assert.True(otherThread.Wait(TimeSpan.FromSeconds(10)), "The other thread's load is blocked: the Gate is held during a load.");
            }
        };

        var userA = cellA.LoadFromAssemblyName(new AssemblyName("Fixture.UserA"));

        Assert.Equal("OK: Dep 1.0 in 'A'", CellTestHelpers.Call(userA, "FixtureUserA.UserA", "UseD"));
        Assert.Equal("OK: Dep 2.0 in 'B'", CellTestHelpers.Call(await otherThread!, "FixtureUserB.UserB", "UseD"));
    }

    [Fact]
    public void AFailedFirstAnswer_IsRevertedSoALaterRequestDecidesAgain()
    {
        var (environment, _) = CellTestHelpers.CreateEnvironment();
        var plan = new CellPlan();
        plan.Private["Fixture.Late"] = new PrivateAssembly("Fixture.Late", Path.Combine(fixtures.OutputDirectory, "missing", "Fixture.Late.dll"), "Fixture.Late", NuGetVersion.Parse("1.0.0"));
        var cell = environment.CreateCell("revert", plan);

        Assert.ThrowsAny<IOException>(() => cell.LoadFromAssemblyName(new AssemblyName("Fixture.Late")));
        // Not frozen: Committed shows the plan again (a failure only shows where nothing is planned).
        Assert.Contains("missing", cell.GetCommittedAnswers().Assemblies["Fixture.Late"].Path);

        var fixedPlan = new CellPlan();
        AddPrivate(fixedPlan, "Fixture.Late");
        cell.MergePlan(fixedPlan);

        var late = cell.LoadFromAssemblyName(new AssemblyName("Fixture.Late"));
        Assert.Equal(fixtures.Assembly("Fixture.Late"), late.Location);
        Assert.Equal(fixtures.Assembly("Fixture.Late"), cell.GetCommittedAnswers().Assemblies["Fixture.Late"].Path);
        Assert.Equal("revert", AssemblyLoadContext.GetLoadContext(late)!.Name);
    }

    private void AddPrivate(CellPlan plan, string project) =>
        plan.Private[project] = new PrivateAssembly(project, fixtures.Assembly(project), project, NuGetVersion.Parse("1.0.0"));

    private PrivateAssembly DepAsset(int version) =>
        new(Dep, fixtures.Assembly($"Fixture.Dep.V{version}", Dep), Dep, NuGetVersion.Parse($"{version}.0.0"));
}

using System.Reflection;
using System.Runtime.Loader;
using Alcove.Cells;
using Alcove.Install;
using Alcove.IntegrationTests.Infrastructure;
using NuGet.Versioning;

namespace Alcove.IntegrationTests.Cells;

/// <summary>
/// The hot-load spike's scenarios (spike/hot-load-into-loaded-compartment), run against the real
/// <see cref="Cell"/> with real assemblies. Where the spike showed the runtime's unsafe behaviour, these tests
/// show that answer freezing and the version check turn it into a loud, early failure.
/// </summary>
[Collection(CellFixturesCollection.Name)]
public sealed class CellScenarioTests(CellFixtures fixtures)
{
    private const string Dep = "Fixture.Dep";

    [Fact]
    public void Q1ReturnExistingLower_TheVersionCheckRejectsALowerVersionAtBindTime()
    {
        // The spike: Load hands back the already-loaded Dep 1.0 for a request for 2.0; the runtime accepts it
        // and the failure only surfaces later as MissingMethodException. The cell rejects it when binding.
        var (environment, logs) = CellTestHelpers.CreateEnvironment();
        var plan = new CellPlan();
        AddPrivate(plan, "Fixture.UserA");
        AddPrivate(plan, "Fixture.UserB");
        AddDep(plan, 1);
        var cell = environment.CreateCell("q1", plan);

        var userA = cell.LoadFromAssemblyName(new AssemblyName("Fixture.UserA"));
        Assert.Equal("OK: Dep 1.0 in 'q1'", CellTestHelpers.Call(userA, "FixtureUserA.UserA", "UseD"));

        var userB = cell.LoadFromAssemblyName(new AssemblyName("Fixture.UserB"));
        var result = CellTestHelpers.Call(userB, "FixtureUserB.UserB", "UseD");

        Assert.StartsWith("EX: FileLoadException", result);
        Assert.Contains("lower than requested", result);
        Assert.StartsWith("EX: FileLoadException", CellTestHelpers.Call(userB, "FixtureUserB.UserB", "UseNewIn2"));
        Assert.All(logs.WithEventId(1004), e => Assert.Contains("Fixture.Dep 2.0.0.0 was requested", e.Message));
        Assert.Equal(2, logs.WithEventId(1004).Count());   // one per bind attempt: failures aren't cached
        Assert.Equal(AnswerSource.Private, cell.GetCommittedAnswers().Assemblies[Dep].Source);
    }

    [Fact]
    public void Q2SplitIdentity_FreezingKeepsTheHostAnswerSoTheCellNeverHoldsTwoCopies()
    {
        // The spike: plugin A takes Dep from the host; a hot install then points Dep at a private 2.0, plugin B
        // gets it, and one compartment silently holds two Deps. With freezing, B fails loudly instead.
        // Stands in for a host that ships Dep 1.0. Process-wide, so no other test answers Fixture.Dep from the host.
        var hostDep = AssemblyLoadContext.Default.LoadFromAssemblyPath(fixtures.Assembly("Fixture.Dep.V1", Dep));
        var (environment, logs) = CellTestHelpers.CreateEnvironment();
        var plan = new CellPlan();
        AddPrivate(plan, "Fixture.UserA");
        AddPrivate(plan, "Fixture.UserB");
        plan.HostShared.Add(Dep);
        var cell = environment.CreateCell("q2", plan);

        var userA = cell.LoadFromAssemblyName(new AssemblyName("Fixture.UserA"));
        var baseSeenByA = (Type)userA.GetType("FixtureUserA.UserA", throwOnError: true)!.GetProperty("BaseThingType")!.GetValue(null)!;
        Assert.Same(hostDep, baseSeenByA.Assembly);

        // A hot install that bypasses the hot-load rule: Dep becomes private 2.0 in the plan.
        var bypass = new CellPlan();
        AddDep(bypass, 2);
        cell.MergePlan(bypass);

        var userB = cell.LoadFromAssemblyName(new AssemblyName("Fixture.UserB"));
        var result = CellTestHelpers.Call(userB, "FixtureUserB.UserB", "UseD");

        Assert.StartsWith("EX: ", result);
        Assert.DoesNotContain(cell.Assemblies, a => a.GetName().Name == Dep);
        Assert.Equal(AnswerSource.Host, cell.GetCommittedAnswers().Assemblies[Dep].Source);
        Assert.NotEmpty(logs.WithEventId(1003));   // "plan now says ..., keeping ..."
    }

    [Fact]
    public void Q3FailureThenAvailable_AFailureIsNotCachedAndDoesNotBlockALaterAnswer()
    {
        var (environment, _) = CellTestHelpers.CreateEnvironment();
        var plan = new CellPlan();
        AddPrivate(plan, "Fixture.LateUser");
        var cell = environment.CreateCell("q3", plan);
        var lateUser = cell.LoadFromAssemblyName(new AssemblyName("Fixture.LateUser"));

        var first = CellTestHelpers.Call(lateUser, "FixtureLateUser.LateUser", "UseLate");
        Assert.Contains("FileNotFoundException", first);
        Assert.Equal(AnswerSource.Failed, cell.GetCommittedAnswers().Assemblies["Fixture.Late"].Source);

        var install = new CellPlan();
        AddPrivate(install, "Fixture.Late");
        cell.MergePlan(install);

        Assert.Equal("OK: Late says hello", CellTestHelpers.Call(lateUser, "FixtureLateUser.LateUser", "UseLate"));
        Assert.Equal("OK: other: Late says hello", CellTestHelpers.Call(lateUser, "FixtureLateUser.LateUser", "UseLateOther"));
        Assert.Equal(AnswerSource.Private, cell.GetCommittedAnswers().Assemblies["Fixture.Late"].Source);
    }

    [Fact]
    public void Q6NativeRemap_TheFirstNativeAnswerIsFrozenForLaterAssemblies()
    {
        // The spike: after the native table changed, a newly loaded assembly got the other copy. Frozen
        // answers keep the first file for the whole cell.
        SkipUnlessNative();
        var (environment, logs) = CellTestHelpers.CreateEnvironment();
        var plan = new CellPlan();
        AddPrivate(plan, "Fixture.NativeUser1");
        AddPrivate(plan, "Fixture.NativeUser2");
        AddNative(plan, 1);
        var cell = environment.CreateCell("q6", plan);

        var user1 = cell.LoadFromAssemblyName(new AssemblyName("Fixture.NativeUser1"));
        Assert.Equal("OK: 1", CellTestHelpers.Call(user1, "FixtureNativeUser1.NativeUser", "Version"));

        var remap = new CellPlan();
        AddNative(remap, 2);
        cell.MergePlan(remap);

        var user2 = cell.LoadFromAssemblyName(new AssemblyName("Fixture.NativeUser2"));
        Assert.Equal("OK: 1", CellTestHelpers.Call(user2, "FixtureNativeUser2.NativeUser", "Version"));
        var committed = cell.GetCommittedAnswers().NativeLibraries[Path.GetFileName(fixtures.NativeLibrary(1))];
        Assert.Equal(fixtures.NativeLibrary(1), committed.Path);
        Assert.NotEmpty(logs.WithEventId(1003));
    }

    [Fact]
    public void Native_TwoCellsMappingOneFileNameToDifferentFiles_LogASonameCollisionWarning()
    {
        SkipUnlessNative();
        var (environment, logs) = CellTestHelpers.CreateEnvironment();
        var plan1 = new CellPlan();
        AddPrivate(plan1, "Fixture.NativeUser1");
        AddNative(plan1, 1);
        var plan2 = new CellPlan();
        AddPrivate(plan2, "Fixture.NativeUser2");
        AddNative(plan2, 2);
        var cell1 = environment.CreateCell("native-1", plan1);
        var cell2 = environment.CreateCell("native-2", plan2);

        var v1 = CellTestHelpers.Call(cell1.LoadFromAssemblyName(new AssemblyName("Fixture.NativeUser1")), "FixtureNativeUser1.NativeUser", "Version");
        var v2 = CellTestHelpers.Call(cell2.LoadFromAssemblyName(new AssemblyName("Fixture.NativeUser2")), "FixtureNativeUser2.NativeUser", "Version");

        // Loaded by full path, both copies work side by side; the warning is about their own dependencies.
        Assert.Equal(("OK: 1", "OK: 2"), (v1, v2));
        var warning = Assert.Single(logs.WithEventId(1008));
        Assert.Contains("native-1", warning.Message);
        Assert.Contains("native-2", warning.Message);
        Assert.Single(environment.NativeLibraries.Collisions);
    }

    [Fact]
    public void Load_ResolvesInTheDocumentedOrder_AndRecordsEveryAnswer()
    {
        var (environment, logs) = CellTestHelpers.CreateEnvironment();
        var plan = new CellPlan();
        AddPrivate(plan, "Fixture.UserB");
        AddDep(plan, 2);
        var cell = environment.CreateCell("order", plan);

        var userB = cell.LoadFromAssemblyName(new AssemblyName("Fixture.UserB"));
        Assert.Equal("OK: Dep 2.0 in 'order'", CellTestHelpers.Call(userB, "FixtureUserB.UserB", "UseD"));

        var unknown = Assert.Throws<FileNotFoundException>(() => cell.LoadFromAssemblyName(new AssemblyName("Fixture.DoesNotExist")));
        Assert.Contains("Fixture.DoesNotExist", unknown.Message);

        var committed = cell.GetCommittedAnswers().Assemblies;
        Assert.Equal(AnswerReason.CellPrivate, committed["Fixture.UserB"].Reason);
        Assert.Equal(AnswerReason.CellPrivate, committed[Dep].Reason);
        Assert.Equal(AnswerReason.HostOwned, committed["System.Runtime"].Reason);
        Assert.Equal(AnswerSource.Failed, committed["Fixture.DoesNotExist"].Source);
        Assert.NotEmpty(logs.WithEventId(1002));   // unknown, at debug level
    }

    [Fact]
    public void CellCreatedCallbacks_RunBeforeAnythingLoads()
    {
        var (environment, _) = CellTestHelpers.CreateEnvironment();
        var seen = new List<(string Cell, int Assemblies)>();
        environment.AddCellCreatedCallback(cell => seen.Add((cell.Name, cell.Assemblies.Count())));

        environment.CreateCell("created-1", new CellPlan());
        environment.CreateCell("created-2", new CellPlan());

        Assert.Equal([("created-1", 0), ("created-2", 0)], seen);
        Assert.Throws<InvalidOperationException>(() => environment.CreateCell("created-1", new CellPlan()));
    }

    [Fact]
    public void WpfRelevantPrivateAssemblies_IdenticalPackagesAreLoadedOnceForAllCells()
    {
        var (environment, logs) = CellTestHelpers.CreateEnvironment();
        CellPlan Plan(bool wpfRelevant)
        {
            var plan = new CellPlan();
            plan.Private[Dep] = DepAsset(2) with { WpfRelevant = wpfRelevant };
            return plan;
        }

        var shared1 = environment.CreateCell("wpf-1", Plan(wpfRelevant: true)).LoadFromAssemblyName(new AssemblyName(Dep));
        var wpf2 = environment.CreateCell("wpf-2", Plan(wpfRelevant: true));
        var shared2 = wpf2.LoadFromAssemblyName(new AssemblyName(Dep));
        var isolated = environment.CreateCell("plain-1", Plan(wpfRelevant: false)).LoadFromAssemblyName(new AssemblyName(Dep));

        Assert.Same(shared1, shared2);
        Assert.Equal("wpf-1", AssemblyLoadContext.GetLoadContext(shared2)!.Name);
        Assert.NotSame(shared1, isolated);
        Assert.Single(logs.WithEventId(1006));
        Assert.Equal(AnswerReason.SharedWpfRelevant, wpf2.GetCommittedAnswers().Assemblies[Dep].Reason);
    }

    [Fact]
    public void Native_AHostAnswerForARequestedName_ConflictsWithAPlanThatAddsOneOfItsFiles()
    {
        // Review finding 3: the cell answers DllImport("alcovenat") from the host's probing (the library sits next
        // to the assembly); an install bringing libalcovenat.so into the cell must not look harmless, because the
        // frozen answer for "alcovenat" would keep the other copy.
        SkipUnlessNative();
        var directory = new TempDirectory("alcove-native");   // not disposed strictly: the cell keeps its copies loaded
        var assembly = Path.Combine(directory.Path, "Fixture.NativeUser1.dll");
        File.Copy(fixtures.Assembly("Fixture.NativeUser1"), assembly);
        File.Copy(fixtures.NativeLibrary(1), Path.Combine(directory.Path, Path.GetFileName(fixtures.NativeLibrary(1))));
        var (environment, _) = CellTestHelpers.CreateEnvironment();
        var plan = new CellPlan();
        plan.Private["Fixture.NativeUser1"] = new PrivateAssembly("Fixture.NativeUser1", assembly, "Fixture.NativeUser1", NuGetVersion.Parse("1.0.0"));
        var cell = environment.CreateCell("native-host", plan);

        var user = cell.LoadFromAssemblyName(new AssemblyName("Fixture.NativeUser1"));
        Assert.Equal("OK: 1", CellTestHelpers.Call(user, "FixtureNativeUser1.NativeUser", "Version"));

        var install = new CellPlan();
        AddNative(install, 2);
        var conflict = Assert.Single(HotLoadRule.Check("native-host", cell.GetCommittedAnswers(), install));
        Assert.Equal(Path.GetFileName(fixtures.NativeLibrary(2)), conflict.Name);
        Assert.Equal(AnswerSource.Host, conflict.Committed.Source);
        directory.DisposeBestEffort();
    }

    [Fact]
    public void VersionCheck_RejectsALowerVersionBeforeLoadingIt()
    {
        // Review finding 8: the first request needs Dep 2.0, the cell only has 1.0. The lower version must not be
        // left loaded in the cell (a later request would get it).
        var (environment, _) = CellTestHelpers.CreateEnvironment();
        var plan = new CellPlan();
        AddPrivate(plan, "Fixture.UserB");
        AddDep(plan, 1);
        var cell = environment.CreateCell("q8", plan);

        var userB = cell.LoadFromAssemblyName(new AssemblyName("Fixture.UserB"));
        var result = CellTestHelpers.Call(userB, "FixtureUserB.UserB", "UseD");

        Assert.Contains("lower than requested", result);
        Assert.DoesNotContain(cell.Assemblies, a => a.GetName().Name == Dep);
    }

    [Fact]
    public async Task VersionCheck_AFailedReservationIsNotRevertedWhenAConcurrentRequestLoadedIt()
    {
        // Review finding 8: UserB's request (Dep 2.0) reserves "private Dep 1.0" and is held before loading;
        // meanwhile UserA's request (Dep 1.0) gets the reserved answer and loads it. UserB's version check then
        // fails, but the answer must stay frozen: Dep 1.0 is loaded and used in the cell.
        var (environment, logs) = CellTestHelpers.CreateEnvironment();
        var plan = new CellPlan();
        AddPrivate(plan, "Fixture.UserA");
        AddPrivate(plan, "Fixture.UserB");
        AddDep(plan, 1);
        var cell = environment.CreateCell("q8-race", plan);
        var userA = cell.LoadFromAssemblyName(new AssemblyName("Fixture.UserA"));
        var userB = cell.LoadFromAssemblyName(new AssemblyName("Fixture.UserB"));

        using var reserved = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = 0;
        environment.LoadingHook = (_, name) =>
        {
            if (name == Dep && Interlocked.CompareExchange(ref holder, Environment.CurrentManagedThreadId, 0) == 0)
            {
                reserved.Set();
                release.Wait(TimeSpan.FromSeconds(30));
            }
        };

        var b = Task.Factory.StartNew(() => CellTestHelpers.Call(userB, "FixtureUserB.UserB", "UseD"), TestContext.Current.CancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Assert.True(reserved.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        var a = CellTestHelpers.Call(userA, "FixtureUserA.UserA", "UseD");
        release.Set();
        var bResult = await b;
        environment.LoadingHook = null;

        Assert.Equal("OK: Dep 1.0 in 'q8-race'", a);
        Assert.Contains("lower than requested", bResult);

        // The answer stayed frozen: a plan change doesn't make the cell answer Dep again (with a second copy).
        var bypass = new CellPlan();
        AddDep(bypass, 2);
        cell.MergePlan(bypass);
        Assert.Contains("lower than requested", CellTestHelpers.Call(userB, "FixtureUserB.UserB", "UseD"));
        Assert.NotEmpty(logs.WithEventId(1003));
        Assert.Single(cell.Assemblies, x => x.GetName().Name == Dep);
    }

    [Fact]
    public void Logging_NeverRunsUnderTheGate()
    {
        // Review finding 9: logger providers are app code; they must not run while the process-wide Gate is held.
        var (environment, logs) = CellTestHelpers.CreateEnvironment();
        var underGate = new List<LogEntry>();
        logs.Observer = e =>
        {
            if (environment.Gate.IsHeldByCurrentThread)
            {
                lock (underGate)
                {
                    underGate.Add(e);
                }
            }
        };
        var plan = new CellPlan();
        AddPrivate(plan, "Fixture.UserA");
        AddPrivate(plan, "Fixture.UserB");
        AddDep(plan, 1);
        Cell cell;
        lock (environment.Gate)
        {
            cell = environment.RegisterCell("logging", plan);   // as a hot install does, under the Gate
        }

        environment.RunCellCreated(cell);
        var userA = cell.LoadFromAssemblyName(new AssemblyName("Fixture.UserA"));
        Assert.Equal("OK: Dep 1.0 in 'logging'", CellTestHelpers.Call(userA, "FixtureUserA.UserA", "UseD"));
        Assert.Throws<FileNotFoundException>(() => cell.LoadFromAssemblyName(new AssemblyName("Fixture.DoesNotExist")));
        var bypass = new CellPlan();
        AddDep(bypass, 2);
        cell.MergePlan(bypass);
        var userB = cell.LoadFromAssemblyName(new AssemblyName("Fixture.UserB"));
        Assert.StartsWith("EX: ", CellTestHelpers.Call(userB, "FixtureUserB.UserB", "UseD"));

        foreach (var id in new[] { 1000, 1001, 1002, 1003, 1004, 1005 })
        {
            Assert.True(logs.WithEventId(id).Any(), $"event {id} wasn't logged");
        }

        Assert.Empty(underGate);
    }

    private void SkipUnlessNative()
    {
        if (fixtures.NativeUnavailableReason is { } reason)
        {
            Assert.Skip($"Native scenario skipped: {reason}.");
        }
    }

    private void AddPrivate(CellPlan plan, string project) =>
        plan.Private[project] = new PrivateAssembly(project, fixtures.Assembly(project), project, NuGetVersion.Parse("1.0.0"));

    private PrivateAssembly DepAsset(int version) =>
        new(Dep, fixtures.Assembly($"Fixture.Dep.V{version}", Dep), Dep, NuGetVersion.Parse($"{version}.0.0"));

    private void AddDep(CellPlan plan, int version) => plan.Private[Dep] = DepAsset(version);

    private void AddNative(CellPlan plan, int version)
    {
        var path = fixtures.NativeLibrary(version);
        plan.NativeLibraries[Path.GetFileName(path)] = new NativeLibraryAsset(Path.GetFileName(path), path, "Fixture.Native", NuGetVersion.Parse($"{version}.0.0"));
    }
}

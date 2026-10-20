using Alcove.Lifecycle;
using Alcove.Storage;
using NuGet.Versioning;

namespace Alcove.Tests.Lifecycle;

public sealed class StagedChangesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Apply_SwitchesVersions_KeepsTheOldOneAsPrevious_AndTrims()
    {
        var state = State(Plugin("A", "2.0.0", previous: ["1.0.0"]) with
        {
            StagedChange = new StagedChange { Kind = StagedChangeKind.Update, TargetVersion = V("3.0.0"), Target = Plugin("A", "3.0.0", dependency: "Lib/3.0.0") },
        });

        var applied = StagedChanges.Apply(state, versionsToKeep: 1)!;

        var a = Assert.Single(applied.Plugins);
        Assert.Equal(("3.0.0", "Lib"), (a.Version.ToNormalizedString(), a.Dependencies.Single().Id));
        Assert.Equal(["2.0.0"], a.PreviousVersions.Select(p => p.Version.ToNormalizedString()));
        Assert.Null(a.StagedChange);
        Assert.Equal(["2.0.0", "1.0.0"], StagedChanges.Apply(state, versionsToKeep: 5)!.Plugins[0].PreviousVersions.Select(p => p.Version.ToNormalizedString()));
        Assert.Empty(StagedChanges.Apply(state, versionsToKeep: 0)!.Plugins[0].PreviousVersions);
    }

    [Fact]
    public void Apply_DropsUninstalls_FlipsEnabledFlags_AndReturnsNullWhenNothingChanges()
    {
        var state = State(
            Plugin("A", "1.0.0") with { StagedChange = new StagedChange { Kind = StagedChangeKind.Uninstall } },
            Plugin("B", "1.0.0") with { StagedEnabled = false },
            Plugin("C", "1.0.0"));

        var applied = StagedChanges.Apply(state, 1)!;

        Assert.Equal(["B", "C"], applied.Plugins.Select(p => p.Id));
        Assert.Equal((false, (bool?)null), (applied.Plugins[0].Enabled, applied.Plugins[0].StagedEnabled));
        Assert.Null(StagedChanges.Apply(applied, 1));
        Assert.NotNull(StagedChanges.Apply(State(Plugin("A", "2.0.0", previous: ["1.0.0"])), 0));   // trimming is a change
    }

    [Fact]
    public void Stage_RecordsTheDifferenceToTheDesiredState_AndEffectiveGivesItBack()
    {
        var current = State(Plugin("A", "1.0.0"), Plugin("B", "1.0.0"), Plugin("C", "1.0.0"));
        var desired = State(Plugin("A", "2.0.0"), Plugin("B", "1.0.0") with { Enabled = false }, Plugin("D", "1.0.0"));

        var staged = StagedChanges.Stage(current, desired, new StageRequest("A", StagedChangeKind.Update), Now);

        Assert.Equal(["A", "B", "C", "D"], staged.Plugins.Select(p => p.Id));
        Assert.Equal((StagedChangeKind.Update, "1.0.0", "2.0.0"), (staged.Plugins[0].StagedChange!.Kind, staged.Plugins[0].Version.ToNormalizedString(), staged.Plugins[0].StagedChange!.TargetVersion!.ToNormalizedString()));
        Assert.Equal((true, (bool?)false), (staged.Plugins[1].Enabled, staged.Plugins[1].StagedEnabled));
        Assert.Equal(StagedChangeKind.Uninstall, staged.Plugins[2].StagedChange!.Kind);
        Assert.Null(staged.Plugins[3].StagedChange);   // a new plugin dependency: installed as it is

        var effective = StagedChanges.Effective(staged);
        Assert.Equal([("A", "2.0.0", true), ("B", "1.0.0", false), ("D", "1.0.0", true)], effective.Plugins.Select(p => (p.Id, p.Version.ToNormalizedString(), p.Enabled)));
        Assert.Equal(["1.0.0"], effective.Find("A")!.PreviousVersions.Select(p => p.Version.ToNormalizedString()));
    }

    [Fact]
    public void Stage_KeepsAnUnchangedStagedChange_AndCancelsOneThatSwitchesBack()
    {
        var current = StagedChanges.Stage(State(Plugin("A", "1.0.0"), Plugin("B", "1.0.0")), State(Plugin("A", "2.0.0"), Plugin("B", "1.0.0")), new StageRequest("A", StagedChangeKind.Update), Now);
        var effective = StagedChanges.Effective(current);

        var disabledB = StagedChanges.Stage(current, effective with { Plugins = [effective.Plugins[0], effective.Plugins[1] with { Enabled = false }] }, new StageRequest("B", StagedChangeKind.Update), Now.AddHours(1));
        Assert.Equal(Now, disabledB.Find("A")!.StagedChange!.StagedAt);

        var rolledBack = StagedChanges.Stage(current, State(effective.Find("A")!.PreviousVersions[0], Plugin("B", "1.0.0")), new StageRequest("A", StagedChangeKind.Rollback), Now);
        Assert.Null(rolledBack.Find("A")!.StagedChange);
        Assert.False(StagedChanges.HasStagedChanges(rolledBack));
    }

    [Fact]
    public void Describe_NamesEachStagedChange()
    {
        var state = State(
            Plugin("A", "1.0.0") with { StagedChange = new StagedChange { Kind = StagedChangeKind.Rollback, TargetVersion = V("0.9.0"), Target = Plugin("A", "0.9.0") }, StagedEnabled = false },
            Plugin("B", "1.0.0") with { StagedChange = new StagedChange { Kind = StagedChangeKind.Uninstall, Reason = "Uninstalled with C (cascade), which it depends on." } });

        var changes = StagedChanges.Describe(state);

        Assert.Equal([PendingChangeKind.Rollback, PendingChangeKind.Disable, PendingChangeKind.Uninstall], changes.Select(c => c.Kind));
        Assert.Equal(("1.0.0", "0.9.0"), (changes[0].CurrentVersion!.ToNormalizedString(), changes[0].NewVersion!.ToNormalizedString()));
        Assert.Null(changes[2].NewVersion);
        Assert.StartsWith("Uninstalled with C", changes[2].Reason, StringComparison.Ordinal);
        Assert.All(changes, c => Assert.True(c.IsStaged));
    }

    [Fact]
    public void ReferencedPackages_CoverActiveStagedAndPreviousVersionsWithTheirPrivateDependencies()
    {
        var state = State(Plugin("A", "2.0.0", previous: ["1.0.0"], dependency: "Lib/2.0.0") with
        {
            StagedChange = new StagedChange { Kind = StagedChangeKind.Update, TargetVersion = V("3.0.0"), Target = Plugin("A", "3.0.0", dependency: "Lib/3.0.0") },
        });

        Assert.Equal(
            ["A 1.0.0", "A 2.0.0", "A 3.0.0", "Lib 2.0.0", "Lib 3.0.0"],
            StagedChanges.ReferencedPackages(state).Select(p => $"{p.Id} {p.Version}").Order(StringComparer.Ordinal));
    }

    private static StateDocument State(params PluginState[] plugins) => new() { Plugins = plugins };

    private static NuGetVersion V(string version) => NuGetVersion.Parse(version);

    private static PluginState Plugin(string id, string version, string[]? previous = null, string? dependency = null) => new()
    {
        Id = id,
        Version = V(version),
        ContentHash = $"{id}-{version}",
        PreviousVersions = (previous ?? []).Select(p => Plugin(id, p)).ToList(),
        Dependencies = dependency is null
            ? []
            : [new ResolvedDependency { Id = dependency.Split('/')[0], Version = V(dependency.Split('/')[1]), Classification = DependencyClass.CellPrivate }],
    };
}

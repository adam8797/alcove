using System.Text;
using System.Text.Json;
using Alcove.Storage;
using Alcove.Tests.TestSupport;
using NuGet.Versioning;

namespace Alcove.Tests.Storage;

public sealed class StateStoreTests
{
    private static readonly DateTime Timestamp = new DateTime(2026, 10, 18, 12, 34, 56, DateTimeKind.Utc).AddTicks(1234567);

    internal static StateDocument FullState() => new()
    {
        Plugins =
        [
            new PluginState
            {
                Id = "Contoso.Charts",
                Version = NuGetVersion.Parse("2.1.0-beta.1"),
                PreviousVersions = [new PluginState { Id = "Contoso.Charts", Version = NuGetVersion.Parse("2.0.0"), ContentHash = "b2xk" }],
                Enabled = false,
                StagedEnabled = true,
                StagedChange = new StagedChange
                {
                    Kind = StagedChangeKind.Update,
                    TargetVersion = NuGetVersion.Parse("2.2.0"),
                    Target = new PluginState { Id = "Contoso.Charts", Version = NuGetVersion.Parse("2.2.0"), ContentHash = "bmV3" },
                    StagedAt = new DateTimeOffset(Timestamp),
                },
                ContentHash = "c2hhNTEyLWNoYXJ0cw==",
                Source = "https://nuget.example.com/v3/index.json",
                Files = [new FileRecord("lib/net10.0/Contoso.Charts.dll", 12345, Timestamp)],
                Assemblies = [new AssemblyRecord { Name = "Contoso.Charts", Path = "lib/net10.0/Contoso.Charts.dll", AssemblyVersion = "2.1.0.0", WpfRelevant = true }],
                Dependencies =
                [
                    new ResolvedDependency
                    {
                        Id = "MyApp.Contracts",
                        Version = NuGetVersion.Parse("2.0.0"),
                        Classification = DependencyClass.HostOwned,
                        RequestedBy = [new DependencyRequest("Contoso.Charts", VersionRange.Parse("[2.0, 3.0)"))],
                    },
                    new ResolvedDependency { Id = "Serilog", Version = NuGetVersion.Parse("3.1.1"), Classification = DependencyClass.HostShared },
                    new ResolvedDependency
                    {
                        Id = "Newtonsoft.Json",
                        Version = NuGetVersion.Parse("13.0.3"),
                        Classification = DependencyClass.CellPrivate,
                        ContentHash = "c2hhNTEyLWpzb24=",
                        Source = "/srv/feed",
                        Files = [new FileRecord("lib/net6.0/Newtonsoft.Json.dll", 700000, Timestamp)],
                        Assemblies = [new AssemblyRecord { Name = "Newtonsoft.Json", Path = "lib/net6.0/Newtonsoft.Json.dll" }],
                    },
                    new ResolvedDependency { Id = "Contoso.Core", Version = NuGetVersion.Parse("1.0.0"), Classification = DependencyClass.Plugin },
                ],
                ExposedDependencies = ["Newtonsoft.Json"],
                Cell = "Contoso.Shared",
                ManifestSchemaVersion = 1,
            },
            new PluginState { Id = "Contoso.Tables", Version = NuGetVersion.Parse("1.0.0"), ContentHash = "dGFibGVz" },
        ],
    };

    [Fact]
    public async Task WriteThenRead_RoundTripsEveryField()
    {
        using var temp = new TempDirectory();
        var store = new StateStore(temp.Path);
        var state = FullState();

        using (var rootLock = await RootLock.AcquireAsync(temp.Path, cancellationToken: TestContext.Current.CancellationToken))
        {
            store.Write(rootLock, state);
        }

        var read = store.Read();
        Assert.Equal(Encoding.UTF8.GetString(StateStore.Serialize(state)), Encoding.UTF8.GetString(StateStore.Serialize(read)));
        var charts = read.Find("contoso.charts")!;
        Assert.Equal(NuGetVersion.Parse("2.1.0-beta.1"), charts.Version);
        Assert.Equal(Timestamp, charts.Files[0].LastWriteTimeUtc);
        Assert.Equal(DateTimeKind.Utc, charts.Files[0].LastWriteTimeUtc.Kind);
        Assert.True(charts.Assemblies[0].WpfRelevant);
        Assert.Equal(StagedChangeKind.Update, charts.StagedChange!.Kind);
        Assert.Equal(("2.2.0", "bmV3"), (charts.StagedChange.Target!.Version.ToNormalizedString(), charts.StagedChange.Target.ContentHash));
        Assert.Equal("2.0.0", Assert.Single(charts.PreviousVersions).Version.ToNormalizedString());
        Assert.True(charts.StagedEnabled);
        Assert.Null(read.Find("Contoso.Tables")!.StagedEnabled);
        Assert.Equal([DependencyClass.HostOwned, DependencyClass.HostShared, DependencyClass.CellPrivate, DependencyClass.Plugin], charts.Dependencies.Select(d => d.Classification));
        Assert.Equal(["Newtonsoft.Json"], charts.ExposedDependencies);
        var request = Assert.Single(charts.Dependencies[0].RequestedBy);
        Assert.Equal(("Contoso.Charts", VersionRange.Parse("[2.0.0, 3.0.0)")), (request.PackageId, request.Range));
        Assert.Contains("\"range\": \"[2.0.0, 3.0.0)\"", Encoding.UTF8.GetString(StateStore.Serialize(read)));
        Assert.True(read.Find("Contoso.Tables")!.Enabled);
        Assert.Equal("https://nuget.example.com/v3/index.json", charts.Source);
        Assert.Equal("/srv/feed", charts.Dependencies[2].Source);
    }

    [Fact]
    public void Serialize_UsesCamelCaseAndEnumNames()
    {
        using var json = JsonDocument.Parse(StateStore.Serialize(FullState()));
        var plugin = json.RootElement.GetProperty("plugins")[0];

        Assert.Equal(StateDocument.CurrentSchemaVersion, json.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("2.1.0-beta.1", plugin.GetProperty("version").GetString());
        Assert.Equal("c2hhNTEyLWNoYXJ0cw==", plugin.GetProperty("contentHash").GetString());
        Assert.Equal("Update", plugin.GetProperty("stagedChange").GetProperty("kind").GetString());
        Assert.Equal("CellPrivate", plugin.GetProperty("dependencies")[2].GetProperty("classification").GetString());
        Assert.Equal(12345, plugin.GetProperty("files")[0].GetProperty("size").GetInt64());
    }

    [Fact]
    public void Read_WithoutAStateFile_ReturnsAnEmptyState()
    {
        using var temp = new TempDirectory();

        Assert.Empty(new StateStore(temp.Combine("missing-root")).Read().Plugins);
    }

    [Fact]
    public void Parse_FillsMissingListsWithEmptyOnes()
    {
        var state = StateStore.Parse("""{ "schemaVersion": 1, "plugins": [ { "id": "A", "version": "1.0.0", "contentHash": "x" } ] }"""u8);

        var plugin = Assert.Single(state.Plugins);
        Assert.Empty(plugin.Dependencies);
        Assert.Empty(plugin.ExposedDependencies);
        Assert.Empty(plugin.Files);
        Assert.True(plugin.Enabled);
        Assert.Null(plugin.Source);   // state files written before sources were recorded
    }

    [Theory]
    [InlineData("""{ "schemaVersion": 2, "plugins": [] }""")]
    [InlineData("""{ "schemaVersion": 0, "plugins": [] }""")]
    [InlineData("""{ "plugins": [ { "id": "A", "version": "not a version", "contentHash": "x" } ] }""")]
    [InlineData("""{ "plugins": [ { "id": "A", "version": "1.0.0" } ] }""")]
    [InlineData("""{ "plugins": [ { "id": "A", "version": "1.0.0", "contentHash": "x", "dependencies": [ { "id": "B", "version": "1.0.0", "classification": "Sometimes" } ] } ] }""")]
    [InlineData("null")]
    [InlineData("{")]
    public void Parse_RejectsMalformedOrNewerFiles(string json) =>
        Assert.Throws<InvalidDataException>(() => StateStore.Parse(Encoding.UTF8.GetBytes(json)));

    [Theory]
    [InlineData("""{ "plugins": [ { "id": "../evil", "version": "1.0.0", "contentHash": "x" } ] }""")]
    [InlineData("""{ "plugins": [ { "id": "A", "version": "1.0.0", "contentHash": "x", "dependencies": [ { "id": "..\\..\\evil", "version": "1.0.0", "classification": "CellPrivate" } ] } ] }""")]
    [InlineData("""{ "plugins": [ { "id": "A", "version": "1.0.0", "contentHash": "x", "dependencies": [ { "id": "a/b", "version": "1.0.0", "classification": "Plugin" } ] } ] }""")]
    [InlineData("""{ "plugins": [ { "id": "A", "version": "2.0.0", "contentHash": "x", "previousVersions": [ { "id": "..", "version": "1.0.0", "contentHash": "y" } ] } ] }""")]
    public void Parse_RejectsPackageIdsThatNuGetWouldNot(string json)
    {
        // Review finding 10: IDs become paths (packages/<id>/<version>, data/<id>).
        var error = Assert.Throws<InvalidDataException>(() => StateStore.Parse(Encoding.UTF8.GetBytes(json)));
        Assert.Contains("invalid package ID", error.Message);
    }

    [Fact]
    public void PackageLayout_RejectsInvalidIdsInPaths()
    {
        var layout = new PackageLayout(Path.GetTempPath());
        Assert.Throws<ArgumentException>(() => layout.GetDataDirectory("../outside"));
        Assert.Throws<ArgumentException>(() => layout.GetPackageDirectory("a/../../b", NuGetVersion.Parse("1.0.0")));
        Assert.EndsWith(Path.Combine("data", "Contoso.Charts"), layout.GetDataDirectory("Contoso.Charts"));
    }

    [Fact]
    public async Task Write_RequiresTheLockOfTheSameRoot()
    {
        using var temp = new TempDirectory();
        using var other = new TempDirectory();
        var store = new StateStore(temp.Path);
        var otherLock = await RootLock.AcquireAsync(other.Path, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Throws<InvalidOperationException>(() => store.Write(otherLock, StateDocument.Empty));
        using (var ownLock = await RootLock.AcquireAsync(temp.Path + Path.DirectorySeparatorChar, cancellationToken: TestContext.Current.CancellationToken))
        {
            new StateStore(temp.Path + Path.DirectorySeparatorChar).Write(ownLock, StateDocument.Empty);   // same root, other spelling
        }

        File.Delete(store.FilePath);
        otherLock.Dispose();
        Assert.Throws<ObjectDisposedException>(() => store.Write(otherLock, StateDocument.Empty));
        Assert.False(File.Exists(store.FilePath));
    }

    [Fact]
    public async Task Write_ReplacesTheFileAtomically()
    {
        using var temp = new TempDirectory();
        var store = new StateStore(temp.Path);
        using var rootLock = await RootLock.AcquireAsync(temp.Path, cancellationToken: TestContext.Current.CancellationToken);

        store.Write(rootLock, FullState());
        store.Write(rootLock, StateDocument.Empty);

        Assert.Empty(store.Read().Plugins);
        Assert.Equal([RootLock.FileName, StateStore.FileName], Directory.GetFiles(temp.Path).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }
}

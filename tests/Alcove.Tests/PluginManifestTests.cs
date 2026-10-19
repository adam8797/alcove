using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace Alcove.Tests;

public sealed class PluginManifestTests
{
    [Fact]
    public void Defaults_AreTheCurrentSchemaVersionAndNoRequests()
    {
        var manifest = new PluginManifest();

        Assert.Equal(PluginManifest.CurrentSchemaVersion, manifest.SchemaVersion);
        Assert.Null(manifest.EntryPoint);
        Assert.Null(manifest.Cell);
        Assert.False(manifest.Isolated);
        Assert.Empty(manifest.PrivateDependencies);
    }

    [Fact]
    public void CurrentSchemaVersion_IsOne() => Assert.Equal(1, PluginManifest.CurrentSchemaVersion);

    [Fact]
    public void CurrentSchemaVersion_MatchesTheVersionAlcoveSdkWrites()
    {
        var targets = Path.Combine(RepoRoot, "src", "Alcove.Sdk", "build", "Alcove.Sdk.targets");
        var sdkVersion = XDocument.Load(targets).Descendants()
            .Single(e => e.Name.LocalName == "AlcovePluginManifestSchemaVersion").Value;

        Assert.Equal(PluginManifest.CurrentSchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), sdkVersion);
    }

    [Fact]
    public void Parse_ReadsTheArchitectureExample()
    {
        // docs/architecture.md §3
        const string json = """
            {
              "schemaVersion": 1,
              "entryPoint": "Contoso.Charts.ChartsPlugin",
              "cell": "Contoso.Shared",
              "isolated": false,
              "privateDependencies": [ "Newtonsoft.Json" ]
            }
            """;

        var manifest = PluginManifest.Parse(json);

        Assert.Equal(1, manifest.SchemaVersion);
        Assert.Equal("Contoso.Charts.ChartsPlugin", manifest.EntryPoint);
        Assert.Equal("Contoso.Shared", manifest.Cell);
        Assert.False(manifest.Isolated);
        Assert.Equal(["Newtonsoft.Json"], manifest.PrivateDependencies);
    }

    [Fact]
    public void Parse_MinimalManifest_UsesDefaults()
    {
        var manifest = PluginManifest.Parse("""{ "schemaVersion": 1 }""");

        Assert.Equal(new PluginManifest(), manifest);
    }

    [Theory]
    [MemberData(nameof(Manifests))]
    public void ToJson_ThenParse_RoundTrips(PluginManifest manifest)
    {
        var json = manifest.ToJson();

        Assert.Equal(manifest, PluginManifest.Parse(json));
        Assert.Equal(manifest, PluginManifest.Read(new MemoryStream(Encoding.UTF8.GetBytes(json))));
    }

    public static TheoryData<PluginManifest> Manifests() =>
    [
        new PluginManifest(),
        new PluginManifest { EntryPoint = "A.Plugin" },
        new PluginManifest { Cell = "Shared", PrivateDependencies = ["Newtonsoft.Json", "Serilog"] },
        new PluginManifest { Isolated = true, EntryPoint = "Outer+Nested`1" },
    ];

    [Fact]
    public void ToJson_UsesCamelCaseAndOmitsNullFields()
    {
        var json = new PluginManifest { Isolated = true }.ToJson();

        using var document = JsonDocument.Parse(json);
        var names = document.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(["schemaVersion", "isolated", "privateDependencies"], names);
    }

    [Fact]
    public void Parse_WithoutSchemaVersion_Throws()
        => Assert.Throws<JsonException>(() => PluginManifest.Parse("""{ "entryPoint": "A.Plugin" }"""));

    [Fact]
    public void Parse_KeepsAnUnsupportedSchemaVersion_ForValidationToReport()
        => Assert.Equal(99, PluginManifest.Parse("""{ "schemaVersion": 99 }""").SchemaVersion);

    [Fact]
    public void Parse_IgnoresUnknownFields()
    {
        var manifest = PluginManifest.Parse("""{ "schemaVersion": 2, "optionalDependencies": [ "X" ], "cell": "C" }""");

        Assert.Equal(new PluginManifest { SchemaVersion = 2, Cell = "C" }, manifest);
    }

    [Fact]
    public void Parse_AllowsCommentsAndTrailingCommas()
        => Assert.True(PluginManifest.Parse("""{ "schemaVersion": 1, /* hand-edited */ "isolated": true, }""").Isolated);

    [Theory]
    [InlineData("""{ "schemaVersion": 1, "privateDependencies": null }""")]
    [InlineData("""{ "schemaVersion": 1, "privateDependencies": [ "" ] }""")]
    [InlineData("""{ "schemaVersion": 1, "privateDependencies": [ null ] }""")]
    [InlineData("""{ "schemaVersion": "1" }""")]
    [InlineData("""{ "schemaVersion": 1, "isolated": "yes" }""")]
    [InlineData("""null""")]
    [InlineData("""{ "schemaVersion": 1""")]
    public void Parse_InvalidManifest_ThrowsJsonException(string json)
        => Assert.Throws<JsonException>(() => PluginManifest.Parse(json));

    [Fact]
    public void Equality_ComparesPrivateDependenciesByContent()
    {
        var a = new PluginManifest { PrivateDependencies = ["A", "B"] };
        var b = new PluginManifest { PrivateDependencies = new List<string> { "A", "B" } };

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, new PluginManifest { PrivateDependencies = ["B", "A"] });
    }

    [Fact]
    public void GetFileName_AppendsTheSuffix()
        => Assert.Equal("Contoso.Charts.plugin.json", PluginManifest.GetFileName("Contoso.Charts"));

    private static string RepoRoot => typeof(PluginManifestTests).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(a => a.Key == "AlcoveRepoRoot").Value!;
}

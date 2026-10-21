using System.Text.Json;
using System.Text.Json.Serialization;

namespace Alcove.Packaging;

/// <summary>
/// The plugin manifest, <c>&lt;PackageId&gt;.plugin.json</c> at the root of a plugin package. It holds what the
/// <c>.nuspec</c> can't: entry point, cell request, isolated flag and cell-private dependencies.
/// </summary>
/// <remarks>
/// <c>Alcove.Sdk</c> generates it; plugin developers don't write it by hand. Display metadata always comes
/// from the <c>.nuspec</c>. Every field except <see cref="SchemaVersion"/> is optional, and unknown fields
/// are ignored so later schema versions can add fields.
/// </remarks>
public sealed record PluginManifest
{
    /// <summary>The manifest schema version this version of Alcove writes.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>The suffix of the manifest file name: <c>&lt;PackageId&gt;.plugin.json</c>.</summary>
    public const string FileNameSuffix = ".plugin.json";

    /// <summary>The manifest schema version. Required.</summary>
    [JsonRequired]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Full name of the <see cref="IPlugin"/> type, or <see langword="null"/> to scan for <see cref="ExtensionAttribute"/> types.</summary>
    public string? EntryPoint { get; init; }

    /// <summary>The named cell the plugin asks to join, or <see langword="null"/> for a default cell. The app's options win.</summary>
    public string? Cell { get; init; }

    /// <summary>Whether the plugin requires a cell to itself.</summary>
    public bool Isolated { get; init; }

    /// <summary>Package IDs of dependencies that are always cell-private, never taken from the host.</summary>
    public IReadOnlyList<string> PrivateDependencies
    {
        get => _privateDependencies;
        // The JSON source generator passes null for a missing field; an explicit JSON null is rejected earlier.
        init => _privateDependencies = value ?? [];
    }

    private readonly IReadOnlyList<string> _privateDependencies = [];

    /// <summary>
    /// The plugin's package ID, as the SDK recorded it in the build output. Optional: it identifies a development
    /// plugin; for an installed plugin the package's own ID is used and this field is ignored.
    /// </summary>
    public string? PackageId { get; init; }

    /// <summary>
    /// The plugin's package version, as the SDK recorded it in the build output. Optional: it identifies a development
    /// plugin; for an installed plugin the package's own version is used and this field is ignored.
    /// </summary>
    public string? Version { get; init; }

    /// <summary>Gets the manifest file name for a plugin package.</summary>
    /// <param name="packageId">The plugin's package ID.</param>
    /// <returns><c>&lt;packageId&gt;.plugin.json</c>.</returns>
    public static string GetFileName(string packageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        return packageId + FileNameSuffix;
    }

    /// <summary>Reads a manifest from UTF-8 JSON.</summary>
    /// <param name="utf8Json">The manifest file's content.</param>
    /// <returns>The manifest. Its schema version is not checked here.</returns>
    /// <exception cref="JsonException">The JSON is malformed, <c>schemaVersion</c> is missing, or a field has the wrong type.</exception>
    public static PluginManifest Read(Stream utf8Json)
    {
        ArgumentNullException.ThrowIfNull(utf8Json);
        return Validate(JsonSerializer.Deserialize(utf8Json, PluginManifestJsonContext.Default.PluginManifest));
    }

    /// <summary>Parses a manifest from a JSON string.</summary>
    /// <param name="json">The manifest file's content.</param>
    /// <returns>The manifest. Its schema version is not checked here.</returns>
    /// <exception cref="JsonException">The JSON is malformed, <c>schemaVersion</c> is missing, or a field has the wrong type.</exception>
    public static PluginManifest Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return Validate(JsonSerializer.Deserialize(json, PluginManifestJsonContext.Default.PluginManifest));
    }

    /// <summary>Writes the manifest as indented JSON, omitting fields that are <see langword="null"/>.</summary>
    /// <returns>The manifest file's content.</returns>
    public string ToJson() => JsonSerializer.Serialize(this, PluginManifestJsonContext.Default.PluginManifest);

    /// <inheritdoc />
    public bool Equals(PluginManifest? other) =>
        other is not null
        && SchemaVersion == other.SchemaVersion
        && string.Equals(EntryPoint, other.EntryPoint, StringComparison.Ordinal)
        && string.Equals(Cell, other.Cell, StringComparison.Ordinal)
        && Isolated == other.Isolated
        && PrivateDependencies.SequenceEqual(other.PrivateDependencies, StringComparer.Ordinal)
        && string.Equals(PackageId, other.PackageId, StringComparison.Ordinal)
        && string.Equals(Version, other.Version, StringComparison.Ordinal);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(SchemaVersion, EntryPoint, Cell, Isolated, PrivateDependencies.Count);

    private static PluginManifest Validate(PluginManifest? manifest)
    {
        if (manifest is null)
        {
            throw new JsonException("The plugin manifest is empty (JSON 'null').");
        }

        foreach (var dependency in manifest.PrivateDependencies)
        {
            if (string.IsNullOrWhiteSpace(dependency))
            {
                throw new JsonException("The plugin manifest's 'privateDependencies' contains an empty entry.");
            }
        }

        return manifest;
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true,
    AllowTrailingCommas = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    RespectNullableAnnotations = true)]
[JsonSerializable(typeof(PluginManifest))]
internal sealed partial class PluginManifestJsonContext : JsonSerializerContext;
